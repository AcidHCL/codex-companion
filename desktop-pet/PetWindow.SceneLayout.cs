using System;
using System.Windows;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private bool _sceneThoughts, _adjustingSceneLayout;
    private double _sceneHeadroom;
    internal double SceneHeadroomDip => _sceneHeadroom;

    // The body keeps its ordinary Size x (Size + 6) viewport. Thoughts extend
    // the same HWND upward; they never take pixels away from the character.
    private void ApplySceneThoughtLayout(bool thoughts, bool preserveFoot = true)
    {
        double bodyHeight = Settings.Size + 6;
        double panelHeight = thoughts ? Math.Ceiling(bodyHeight * .38) : 0;
        double header = thoughts && _bubble.Visibility == Visibility.Visible ? 60 : 0;
        double headroom = panelHeight + header;
        bool changed = Math.Abs(_sceneHeadroom - headroom) > .01;
        Rect before = preserveFoot && changed && _handle != IntPtr.Zero
            ? NativeMethods.GetWindowBoundsPixels(_handle) : Rect.Empty;
        Point oldFoot = before.IsEmpty ? default : VisibleFootPixels;
        _sceneThoughts = thoughts;
        _sceneHeadroom = headroom;
        _adjustingSceneLayout = true;
        try
        {
            Height = Settings.Size + 88 + headroom;
            _character.Margin = new Thickness(12, 46 + headroom, 12, 36);
            if (_scene != null)
            {
                _scene.SetViewportLayout(bodyHeight, panelHeight);
                _scene.Margin = new Thickness(0, -panelHeight, 0, 0);
            }
            UpdateLayout();
            if (!before.IsEmpty)
            {
                Point next = VisibleFootPixels;
                NativeMethods.SetWindowPositionPixels(_handle,
                    (int)Math.Round(before.Left + oldFoot.X - next.X),
                    (int)Math.Round(before.Top + oldFoot.Y - next.Y));
            }
            if (changed) _movement?.ReconfigureGeometry();
        }
        finally { _adjustingSceneLayout = false; }
    }
}
