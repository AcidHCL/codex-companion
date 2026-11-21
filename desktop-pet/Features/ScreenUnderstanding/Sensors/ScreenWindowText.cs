using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace DesktopPet.ScreenUnderstanding;

internal sealed record ScreenTextRead(bool Safe, string Status, string Text);

internal static class ScreenWindowText
{
    private const int MaximumNodes = 256;

    internal static ScreenTextRead Read(ScreenWindowSnapshot window, bool includeText, Stopwatch elapsed)
    {
        try
        {
            var root = AutomationElement.FromHandle(window.Window);
            if (root == null) return new(false, "accessibility_unavailable", "");
            var client = new System.Windows.Rect(window.VisibleClient.X, window.VisibleClient.Y,
                window.VisibleClient.Width, window.VisibleClient.Height);
            // Query the entire target subtree for password controls, even when
            // only a small prefix of its ordinary content can be sampled.
            var passwords = root.FindAll(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.IsPasswordProperty, true));
            foreach (AutomationElement password in passwords)
            {
                var field = password.Current;
                if (!field.IsOffscreen && Visible(field.BoundingRectangle))
                    return new(false, "protected_content", "");
            }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) return new(false, "accessibility_timeout", "");
            if (!includeText) return new(true, "safe", "");

            var walk = TreeWalker.ControlViewWalker;
            var pending = new Queue<AutomationElement>();
            var cache = new CacheRequest();
            cache.Add(AutomationElement.IsPasswordProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.NativeWindowHandleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.IsTextPatternAvailableProperty);
            pending.Enqueue(root.GetUpdatedCache(cache));
            int scheduled = 1;
            var text = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0 && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                var element = pending.Dequeue();
                var current = element.Cached;
                if (current.IsOffscreen) continue;
                bool visible = Visible(current.BoundingRectangle);
                if (visible && current.IsPassword) return new(false, "protected_content", "");
                int handle = current.NativeWindowHandle;
                if (handle != 0 && ScreenCaptureNative.Native.GetAncestor(new IntPtr(handle), 2) != window.Window) continue;
                if (visible && element.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty) is true &&
                    element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern) && pattern is TextPattern document)
                {
                    // Never DocumentRange: hidden/scrolled-away text is outside
                    // the selected current-window observation scope.
                    var ranges = document.GetVisibleRanges();
                    foreach (var range in ranges)
                    {
                        Append(range.GetText(Math.Min(1200, ScreenCapturePolicy.MaximumText - text.Length)));
                        if (text.Length >= ScreenCapturePolicy.MaximumText) break;
                    }
                }
                else if (visible && current.ControlType is { } control && (control == ControlType.Text || control == ControlType.Button ||
                    control == ControlType.MenuItem || control == ControlType.TabItem || control == ControlType.Hyperlink ||
                    control == ControlType.CheckBox || control == ControlType.RadioButton)) Append(element.Current.Name);
                if (text.Length >= ScreenCapturePolicy.MaximumText) break;
                if (scheduled >= MaximumNodes) continue;
                // First collect text from each reachable level without letting
                // one dense container consume the whole worker deadline.
                var child = walk.GetFirstChild(element, cache);
                while (child != null && elapsed.Elapsed < TimeSpan.FromSeconds(3))
                {
                    pending.Enqueue(child);
                    if (++scheduled >= MaximumNodes) break;
                    child = walk.GetNextSibling(child, cache);
                }
            }
            return new(true, "read", ScreenCapturePolicy.BoundText(text.ToString()));

            bool Visible(System.Windows.Rect bounds) => !bounds.IsEmpty &&
                bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(client);

            void Append(string value)
            {
                int left = ScreenCapturePolicy.MaximumText - text.Length;
                if (left <= 0 || string.IsNullOrWhiteSpace(value)) return;
                string clean = ScreenCapturePolicy.BoundText(value, Math.Min(left, 1200));
                if (clean.Length == 0 || !seen.Add(clean)) return;
                if (text.Length > 0 && left > 1) { text.Append('\n'); left--; }
                text.Append(clean.AsSpan(0, Math.Min(left, clean.Length)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, "accessibility_unavailable", ""); }
    }
}
