using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private CharacterGeometry? _live2DGeometry, _sceneGeometry;
    private CharacterGeometry? _builtInContactGeometry;
    private DeepSeekMotionProfile _builtInMotionProfile = DeepSeekMotionTimeline.Legacy;
    private void LoadBuiltInContactGeometry(SceneAnimationPack pack)
    {
        // Reload on every pack selection, including switching back to legacy.
        // Invalid new metadata disables scripted movement instead of applying
        // six-pose coordinates to unrelated twenty/thirty-six-frame artwork.
        try { _builtInMotionProfile = DeepSeekMotionTimeline.ForPack(pack); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            _builtInMotionProfile = DeepSeekMotionTimeline.Unavailable;
            Program.Log(ex);
        }
        // Keep the established support line. Airborne/hand-grip poses must not
        // lower the standing anchor when a new animation is installed.
        Rect visible = Rect.Empty;
        double footY = 0;
        string[] baseline = { "idle", "working", "walk-left", "walk-right", "fall", "land", "drag", "sing" };
        foreach (string relative in pack.Actions.Where(pair => baseline.Contains(pair.Key)).Select(pair => pair.Value).SelectMany(action =>
            (action.Start?.Frames ?? Array.Empty<string>()).Concat(action.Loop.Frames).Concat(action.End?.Frames ?? Array.Empty<string>())).Distinct())
        {
            string path = Path.Combine(pack.RootDirectory, relative);
            visible.Union(FootGeometry.GetPngVisibleBounds(path));
            footY = Math.Max(footY, FootGeometry.GetPngFoot(path).Y);
        }
        Point foot = _builtInMotionProfile.StandingFoot is Point fixedFoot
            ? new Point(fixedFoot.X / _builtInMotionProfile.CanvasSize, fixedFoot.Y / _builtInMotionProfile.CanvasSize)
            : new Point(.5, footY);
        _builtInContactGeometry = new CharacterGeometry(foot, visible);
    }

    internal Point? MotionContactPixels(string action, int frame)
    {
        if (!UsesBuiltInScenes || _builtInMotionProfile.ContactPoint(action, frame) is not Point contact) return null;
        var rect = CharacterRectDip;
        double side = Math.Min(rect.Width, rect.Height);
        double left = rect.Left + (rect.Width - side) * CurrentScenePack!.AnchorX;
        double top = rect.Top + (rect.Height - side) * CurrentScenePack.AnchorY;
        return ToPixels(new Point(left + contact.X / _builtInMotionProfile.CanvasSize * side, top + contact.Y / _builtInMotionProfile.CanvasSize * side));
    }
    private DispatcherTimer? _footGeometryTimer;
    private bool _samplingFootGeometry;
    internal Point VisibleFootPixels => ToPixels(GetCharacterGeometryDip().Foot);
    internal Rect CharacterVisibleBoundsPixels
    {
        get
        {
            var geometry = GetCharacterGeometryDip();
            double scale = NativeMethods.GetDpiScale(_handle);
            return new Rect(geometry.Bounds.X * scale, geometry.Bounds.Y * scale,
                geometry.Bounds.Width * scale, geometry.Bounds.Height * scale);
        }
    }

    private Point ToPixels(Point point)
    {
        double scale = NativeMethods.GetDpiScale(_handle);
        return new Point(point.X * scale, point.Y * scale);
    }

    private Rect CharacterRectDip => new(_character.Margin.Left, _character.Margin.Top,
        _character.ActualWidth > 0 ? _character.ActualWidth : Settings.Size,
        _character.ActualHeight > 0 ? _character.ActualHeight : Settings.Size + 6);

    private CharacterGeometry GetCharacterGeometryDip()
    {
        var rect = CharacterRectDip;
        if (_sceneActive && UsesBuiltInScenes && _builtInContactGeometry is CharacterGeometry builtIn)
        {
            // Thought text has its own space above this unchanged body rect.
            // It must not affect collisions, window support, or model scale.
            double side = Math.Min(rect.Width, rect.Height);
            double left = rect.Left + (rect.Width - side) * CurrentScenePack!.AnchorX;
            double top = rect.Top + (rect.Height - side) * CurrentScenePack.AnchorY;
            var bounds = new Rect(left + builtIn.Bounds.X * side, top + builtIn.Bounds.Y * side,
                builtIn.Bounds.Width * side, builtIn.Bounds.Height * side);
            double footY = builtIn.Foot.Y;
            // During the two grounded jump stages only, preserve real shoe
            // contact despite the redraw's crouch/stand canvas offsets. Tucked
            // airborne feet keep their authored motion relative to the body.
            if (_movement?.Grounded == true && _movement.MotionAction is string action
                && action.StartsWith("jump-", StringComparison.Ordinal)
                && _sceneAction == action && _sceneTransitionCommand == 0
                && _builtInMotionProfile.Clip(action)?.GroundFootY is double[] groundFeet
                && _movement.MotionFrame >= 0 && _movement.MotionFrame < groundFeet.Length)
                footY = groundFeet[_movement.MotionFrame] / _builtInMotionProfile.CanvasSize;
            return new CharacterGeometry(new Point(left + builtIn.Foot.X * side, top + footY * side), bounds);
        }
        CharacterGeometry? viewportGeometry = _sceneActive ? _sceneGeometry : CurrentLive2DModel != null ? _live2DGeometry : null;
        if (viewportGeometry is CharacterGeometry normalized)
            return new CharacterGeometry(new Point(rect.X + normalized.Foot.X * rect.Width, rect.Y + normalized.Foot.Y * rect.Height),
                new Rect(rect.X + normalized.Bounds.X * rect.Width, rect.Y + normalized.Bounds.Y * rect.Height,
                    normalized.Bounds.Width * rect.Width, normalized.Bounds.Height * rect.Height));
        if (_image.Source is BitmapSource bitmap)
            return FootGeometry.Fit(new Size(bitmap.PixelWidth, bitmap.PixelHeight), FootGeometry.FromBitmap(bitmap), rect);
        return new CharacterGeometry(new Point(rect.Left + rect.Width / 2, rect.Bottom), rect);
    }

    private void UpdatePngFootPivot()
    {
        if (_image.Source is not BitmapSource bitmap) return;
        var fitted = FootGeometry.Fit(new Size(bitmap.PixelWidth, bitmap.PixelHeight), FootGeometry.FromBitmap(bitmap), CharacterRectDip);
        var offset = VisualTreeHelper.GetOffset(_image);
        double width = _image.ActualWidth, height = _image.ActualHeight;
        if (width > 0 && height > 0)
            _image.RenderTransformOrigin = new Point((fitted.Foot.X - _character.Margin.Left - offset.X) / width,
                (fitted.Foot.Y - _character.Margin.Top - offset.Y) / height);
    }

    private void QueueFootGeometryRefresh()
    {
        if (_closed || DisplayRenderingPaused) return;
        UpdatePngFootPivot();
        _footGeometryTimer ??= CreateFootTimer();
        _footGeometryTimer.Stop(); _footGeometryTimer.Start();
    }

    private DispatcherTimer CreateFootTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RefreshFootGeometryAsync();
        };
        return timer;
    }

    private async Task RefreshFootGeometryAsync()
    {
        if (_closed || !IsVisible || DisplayRenderingPaused) return;
        if (_samplingFootGeometry) { QueueFootGeometryRefresh(); return; }
        _samplingFootGeometry = true;
        var scene = _scene;
        var live2D = _live2D;
        int command = _sceneCommand, modelGeneration = _modelGeneration;
        try
        {
            if (_sceneActive && UsesBuiltInScenes)
            {
                // Known PNG geometry is stable across every state; no asynchronous screenshot is needed.
            }
            else if (_sceneActive && scene != null)
            {
                var measured = await scene.CaptureContactGeometryAsync();
                if (!_closed && _sceneActive && ReferenceEquals(scene, _scene) && command == _sceneCommand) _sceneGeometry = measured;
            }
            else if (CurrentLive2DModel != null && live2D != null)
            {
                var measured = await live2D.CaptureContactGeometryAsync();
                if (!_closed && !_sceneActive && ReferenceEquals(live2D, _live2D) && modelGeneration == _modelGeneration) _live2DGeometry = measured;
            }
            if (!_closed && !DisplayRenderingPaused && Settings.MovementEnabled) _movement?.ReconfigureGeometry();
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            // Contact measurement is optional; renderer replacement/COM failures keep the previous geometry.
            if (!_closed && ReferenceEquals(scene, _scene) && ReferenceEquals(live2D, _live2D)) Program.Log(ex);
        }
        finally { _samplingFootGeometry = false; }
    }

    private void UpdateBadgePlacement()
    {
        ApplySceneThoughtLayout(_sceneThoughts);
        _stateBadge.VerticalAlignment = VerticalAlignment.Top;
        _stateBadge.Margin = new Thickness(12, 20, 12, 0);
        _stateBadge.Visibility = Settings.ShowStatusBadge && _bubble.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClampPetToVisibleWorkArea()
    {
        if (_handle == IntPtr.Zero) return;
        var bounds = NativeMethods.GetWindowBoundsPixels(_handle);
        var visible = CharacterVisibleBoundsPixels;
        var foot = VisibleFootPixels;
        var work = NativeMethods.GetWindowWorkAreaPixels(_handle);
        // The transparent HWND border may extend over the taskbar; the feet must not float above it.
        double x = Math.Clamp(bounds.Left, work.Left - visible.Left, Math.Max(work.Left - visible.Left, work.Right - visible.Right));
        double y = Math.Clamp(bounds.Top, work.Top - visible.Top, Math.Max(work.Top - visible.Top, work.Bottom - foot.Y));
        NativeMethods.SetWindowPositionPixels(_handle, (int)Math.Round(x), (int)Math.Round(y));
    }
}
