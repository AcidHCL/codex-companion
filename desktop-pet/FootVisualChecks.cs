using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

internal static class FootVisualChecks
{
    private sealed record Sample(double ScaleY, double PivotErrorPixels, double ContactErrorPixels,
        double TransformedFootY, int RasterBottom, double PhysicsFootY);

    // Use only the already loaded, non-persisting PNG test pet. This probes WPF's
    // actual visual tree and raster output, rather than reproducing the layout
    // calculation in FootGeometry. No input is injected and no app is launched.
    public static async Task<object> RunAsync(PetWindow pet)
    {
        pet.Dispatcher.VerifyAccess();
        IntPtr hwnd = new WindowInteropHelper(pet).Handle;
        if (!pet.IsLoaded || hwnd == IntPtr.Zero || pet.CurrentLive2DModel != null || pet.IsScenePlaying)
            throw new InvalidOperationException("Foot visual checks require a loaded PNG test pet without an active scene.");
        if (!SystemParameters.ClientAreaAnimation)
            throw new InvalidOperationException("Foot visual checks need system UI animations enabled to exercise the actual breathing clock.");
        var originalWindow = NativeMethods.GetWindowBoundsPixels(hwnd);
        double originalSize = pet.Settings.Size;
        bool originalMotion = pet.Settings.MotionEnabled, originalMovement = pet.Settings.MovementEnabled;
        bool originalVisible = pet.IsVisible, originalSceneAuto = pet.Settings.SceneAutoEnabled;
        var checks = new List<string>();
        var measurements = new List<object>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("WPF foot visual check failed: " + label);
            checks.Add(label);
        }
        try
        {
            pet.SetSceneAuto(false);
            pet.SetMovement(false); // also cancels any existing click bounce
            pet.SetMotion(false);
            if (!pet.IsVisible) pet.Show();
            foreach (int requestedSize in new[] { 160, 420 })
            {
                pet.SetSize(requestedSize);
                pet.SetMotion(false);
                pet.UpdateLayout();
                await pet.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(220); // let the queued geometry refresh and render complete
                var image = FindImage(pet) ?? throw new InvalidOperationException("PNG image visual was not found.");
                var bitmap = image.Source as BitmapSource ?? throw new InvalidOperationException("PNG bitmap is unavailable.");
                var normalized = FootGeometry.FromBitmap(bitmap);
                Rect drawing = VisualTreeHelper.GetContentBounds(image);
                Require(!drawing.IsEmpty && drawing.Width > 0 && drawing.Height > 0,
                    requestedSize + " DIP actual Image drawing has measurable content");
                var scale = image.RenderTransform is TransformGroup transforms
                    ? transforms.Children.OfType<ScaleTransform>().FirstOrDefault() : null;
                Require(scale != null, requestedSize + " DIP uses the production ScaleTransform breathing animation");
                double dpi = NativeMethods.GetDpiScale(hwnd);
                Sample ReadSample()
                {
                    // ImageDrawing's actual destination rectangle already includes
                    // WPF's image arrange/Stretch behavior; source pixels map here.
                    Point sourceFoot = new(drawing.Left + normalized.Foot.X * drawing.Width,
                        drawing.Top + normalized.Foot.Y * drawing.Height);
                    Point pivot = new(image.RenderTransformOrigin.X * image.ActualWidth,
                        image.RenderTransformOrigin.Y * image.ActualHeight);
                    GeneralTransform actualTransform = image.TransformToAncestor(pet);
                    Point renderedFoot = actualTransform.Transform(sourceFoot);
                    Point physicsFoot = pet.VisibleFootPixels;
                    double pivotError = (sourceFoot - pivot).Length * dpi;
                    double contactError = (new Point(renderedFoot.X * dpi, renderedFoot.Y * dpi) - physicsFoot).Length;
                    int bottom = RasterFootBottom(pet, image, drawing, renderedFoot, dpi);
                    return new Sample(scale!.ScaleY, pivotError, contactError, renderedFoot.Y * dpi, bottom, physicsFoot.Y);
                }
                var baseline = ReadSample();
                Require(Math.Abs(baseline.ScaleY - 1) < .000001, requestedSize + " DIP breathing-off state is unscaled");
                Require(baseline.PivotErrorPixels <= .25 && baseline.ContactErrorPixels <= .25,
                    requestedSize + " DIP actual WPF source sole, transform pivot and physics contact coincide" +
                    $" (pivot error={baseline.PivotErrorPixels:F3}px; contact error={baseline.ContactErrorPixels:F3}px)");
                Require(Math.Abs(baseline.RasterBottom - baseline.PhysicsFootY) <= 1.25,
                    requestedSize + " DIP rendered alpha sole agrees with physics contact within one raster pixel plus subpixel rounding" +
                    $" (raster={baseline.RasterBottom}px; contact={baseline.PhysicsFootY:F3}px)");

                pet.SetMotion(true);
                var samples = new List<Sample> { baseline };
                for (int i = 0; i < 19; i++)
                {
                    await Task.Delay(90);
                    samples.Add(ReadSample());
                }
                Require(samples.Max(x => x.ScaleY) - samples.Min(x => x.ScaleY) >= .005,
                    requestedSize + " DIP actual animation clock advances through visible breathing deformation");
                Require(samples.Max(x => x.PivotErrorPixels) <= .25 && samples.Max(x => x.ContactErrorPixels) <= .25,
                    requestedSize + " DIP visual sole stays fixed throughout actual breathing samples");
                Require(samples.Max(x => x.RasterBottom) - samples.Min(x => x.RasterBottom) <= 1,
                    requestedSize + " DIP rasterized sole changes by at most one antialiasing pixel while breathing");
                Require(samples.Max(x => Math.Abs(x.RasterBottom - x.PhysicsFootY)) <= 1.25,
                    requestedSize + " DIP rendered sole stays on the physics contact during breathing");
                pet.SetMotion(false);
                await Task.Delay(100);
                var stopped = ReadSample();
                Require(Math.Abs(stopped.ScaleY - 1) < .000001 && stopped.ContactErrorPixels <= .25 &&
                    Math.Abs(stopped.RasterBottom - baseline.RasterBottom) <= 1,
                    requestedSize + " DIP disabling breathing returns to the same foot position");
                measurements.Add(new
                {
                    sizeDip = requestedSize, dpiScale = dpi, imageWidth = image.ActualWidth, imageHeight = image.ActualHeight,
                    drawingX = drawing.X, drawingY = drawing.Y, drawingWidth = drawing.Width, drawingHeight = drawing.Height,
                    pivotX = image.RenderTransformOrigin.X, pivotY = image.RenderTransformOrigin.Y,
                    normalizedFootX = normalized.Foot.X, normalizedFootY = normalized.Foot.Y,
                    visibleBounds = pet.CharacterVisibleBoundsPixels.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    baseline, stopped, samples
                });
            }
            return new { passed = true, count = checks.Count, checks, measurements, actualWpfRasterization = true,
                syntheticMouseOrKeyboardInput = false, additionalProcesses = 0, persisted = false };
        }
        finally
        {
            pet.SetMotion(false);
            pet.SetMovement(false);
            pet.SetSize(originalSize);
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(originalWindow.X), (int)Math.Round(originalWindow.Y));
            pet.MovementController?.NotifyExternalMove();
            pet.SetMotion(originalMotion);
            if (originalVisible) pet.Show(); else pet.Hide();
            pet.SetSceneAuto(originalSceneAuto);
            pet.SetMovement(originalMovement);
        }
    }

    private static Image? FindImage(DependencyObject root)
    {
        if (root is Image image && image.Source is BitmapSource && image.Visibility == Visibility.Visible) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var candidate = FindImage(VisualTreeHelper.GetChild(root, i));
            if (candidate != null) return candidate;
        }
        return null;
    }

    private static int RasterFootBottom(PetWindow pet, Image image, Rect drawing, Point renderedFoot, double dpi)
    {
        int width = Math.Max(1, (int)Math.Ceiling(pet.ActualWidth * dpi));
        int height = Math.Max(1, (int)Math.Ceiling(pet.ActualHeight * dpi));
        var target = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        target.Render(pet);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        target.CopyPixels(pixels, stride, 0);
        GeneralTransform transform = image.TransformToAncestor(pet);
        // Same central body band as the source measurement, but sample the final
        // WPF output after scaling, layout, transforms and DPI rasterization.
        double x0 = transform.Transform(new Point(drawing.Left + drawing.Width * .2, drawing.Top)).X * dpi;
        double x1 = transform.Transform(new Point(drawing.Left + drawing.Width * .8, drawing.Top)).X * dpi;
        int left = Math.Clamp((int)Math.Floor(Math.Min(x0, x1)), 0, width - 1);
        int right = Math.Clamp((int)Math.Ceiling(Math.Max(x0, x1)), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling((renderedFoot.Y + 12) * dpi), 0, height - 1);
        int top = Math.Clamp((int)Math.Floor((renderedFoot.Y - Math.Max(20, drawing.Height * .12)) * dpi), 0, bottom);
        for (int y = bottom; y >= top; y--)
        {
            int visible = 0;
            for (int x = left; x < right; x++)
                if (pixels[y * stride + x * 4 + 3] >= 16 && ++visible >= 2) return y + 1;
        }
        throw new InvalidOperationException("WPF output contains no visible pixels near the expected sole.");
    }
}
