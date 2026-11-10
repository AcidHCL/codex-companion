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
            var walk = TreeWalker.ControlViewWalker;
            var pending = new Queue<AutomationElement>();
            var readable = new List<AutomationElement>();
            pending.Enqueue(root);
            int count = 0;
            while (pending.Count > 0)
            {
                if (++count > MaximumNodes || elapsed.Elapsed > TimeSpan.FromSeconds(4))
                    return new(false, "accessibility_limit", "");
                var element = pending.Dequeue();
                var current = element.Current;
                if (current.IsOffscreen) continue;
                var bounds = current.BoundingRectangle;
                bool visible = !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 &&
                    bounds.IntersectsWith(new System.Windows.Rect(window.VisibleClient.X, window.VisibleClient.Y,
                        window.VisibleClient.Width, window.VisibleClient.Height));
                if (visible && current.IsPassword) return new(false, "protected_content", "");
                int handle = current.NativeWindowHandle;
                if (handle != 0 && ScreenCaptureNative.Native.GetAncestor(new IntPtr(handle), 2) != window.Window) continue;
                // Some editors/terminals expose their visible TextPattern on
                // the root itself. Window.Name is not one of the text fallbacks.
                if (visible) readable.Add(element);
                // Containers with empty bounds can still hold visible controls.
                // Providers with many hidden nodes are bounded by the same cap.
                var child = walk.GetFirstChild(element);
                while (child != null)
                {
                    if (pending.Count + count > MaximumNodes) return new(false, "accessibility_limit", "");
                    pending.Enqueue(child); child = walk.GetNextSibling(child);
                }
            }
            if (!includeText) return new(true, "safe", "");
            var text = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in readable)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(4)) return new(false, "accessibility_timeout", "");
                var current = element.Current;
                if (current.IsPassword) return new(false, "protected_content", "");
                if (current.IsOffscreen) continue;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern) && pattern is TextPattern document)
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
                else if (current.ControlType is { } control && (control == ControlType.Text || control == ControlType.Button ||
                    control == ControlType.MenuItem || control == ControlType.TabItem || control == ControlType.Hyperlink ||
                    control == ControlType.CheckBox || control == ControlType.RadioButton)) Append(current.Name);
                if (text.Length >= ScreenCapturePolicy.MaximumText) break;
            }
            return new(true, "read", ScreenCapturePolicy.BoundText(text.ToString()));

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
