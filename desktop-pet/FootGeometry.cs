using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

internal readonly record struct CharacterGeometry(Point Foot, Rect Bounds);

// Alpha measurements use source-image coordinates; layout conversion happens
// separately so resizing or DPI changes never rescan the bitmap on a render tick.
internal static class FootGeometry
{
    private const byte VisibleAlpha = 16;
    private sealed record Measurement(Point Foot, Rect Bounds);
    private sealed record FileMeasurement(long Length, long Modified, Measurement Value);
    private static readonly ConditionalWeakTable<BitmapSource, Measurement> BitmapCache = new();
    private static readonly Dictionary<string, FileMeasurement> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileCacheLock = new();

    public static Point GetPngFoot(BitmapSource bitmap) => Measure(bitmap).Foot;
    public static Rect GetPngVisibleBounds(BitmapSource bitmap) => Measure(bitmap).Bounds;
    public static Point GetPngFoot(string filename) => MeasureFile(filename).Foot;
    public static Rect GetPngVisibleBounds(string filename) => MeasureFile(filename).Bounds;
    public static CharacterGeometry FromBitmap(BitmapSource bitmap)
    {
        var value = Measure(bitmap);
        return new CharacterGeometry(value.Foot, value.Bounds);
    }
    public static CharacterGeometry Fit(Size sourcePixels, CharacterGeometry normalized, Rect characterRect) =>
        FitVisibleGeometry(sourcePixels, normalized.Bounds, normalized.Foot, characterRect);

    public static CharacterGeometry FitVisibleGeometry(Size sourcePixels, Rect normalizedVisibleBounds,
        Point normalizedFoot, Rect containerDip)
    {
        if (!Finite(sourcePixels.Width) || !Finite(sourcePixels.Height) || sourcePixels.Width <= 0 || sourcePixels.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePixels));
        if (containerDip.IsEmpty || !Finite(containerDip.X) || !Finite(containerDip.Y) ||
            !Finite(containerDip.Width) || !Finite(containerDip.Height)) throw new ArgumentOutOfRangeException(nameof(containerDip));
        if (!Finite(normalizedFoot.X) || !Finite(normalizedFoot.Y)) throw new ArgumentOutOfRangeException(nameof(normalizedFoot));
        if (normalizedVisibleBounds.IsEmpty || !Finite(normalizedVisibleBounds.X) || !Finite(normalizedVisibleBounds.Y) ||
            !Finite(normalizedVisibleBounds.Width) || !Finite(normalizedVisibleBounds.Height)) throw new ArgumentOutOfRangeException(nameof(normalizedVisibleBounds));
        double scale = Math.Min(containerDip.Width / sourcePixels.Width, containerDip.Height / sourcePixels.Height);
        double width = sourcePixels.Width * scale, height = sourcePixels.Height * scale;
        double left = containerDip.Left + (containerDip.Width - width) / 2;
        double top = containerDip.Top + (containerDip.Height - height) / 2;
        double x0 = Math.Clamp(normalizedVisibleBounds.Left, 0, 1), x1 = Math.Clamp(normalizedVisibleBounds.Right, 0, 1);
        double y0 = Math.Clamp(normalizedVisibleBounds.Top, 0, 1), y1 = Math.Clamp(normalizedVisibleBounds.Bottom, 0, 1);
        return new CharacterGeometry(
            new Point(left + Math.Clamp(normalizedFoot.X, 0, 1) * width, top + Math.Clamp(normalizedFoot.Y, 0, 1) * height),
            new Rect(left + x0 * width, top + y0 * height, (x1 - x0) * width, (y1 - y0) * height));
    }

    private static bool Finite(double value) => double.IsFinite(value);

    private static Measurement MeasureFile(string filename)
    {
        string path = Path.GetFullPath(filename);
        var file = new FileInfo(path);
        long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
        lock (FileCacheLock)
        {
            if (FileCache.TryGetValue(path, out var previous) && previous.Length == length && previous.Modified == modified)
                return previous.Value;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var measured = Measure(bitmap);
            if (FileCache.Count >= 64) FileCache.Clear();
            FileCache[path] = new FileMeasurement(length, modified, measured);
            return measured;
        }
    }

    private static Measurement Measure(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        // WriteableBitmap may change after a call; retain measurements only for
        // immutable asset bitmaps. The renderer never scans a mutable frame here.
        return bitmap.IsFrozen ? BitmapCache.GetValue(bitmap, Scan) : Scan(bitmap);
    }

    private static Measurement Scan(BitmapSource source)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(source), "Unsupported bitmap dimensions.");
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        int left = width, right = -1, top = height, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < VisibleAlpha) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        if (right < left) return new Measurement(new Point(.5, 1), new Rect(0, 0, 1, 1));

        // The nine skins put the body in the middle of the canvas. Detect the
        // support line in that band, ignoring tails and ornaments in corners.
        // Require more than an isolated edge pixel for normally sized artwork.
        int centerLeft = (int)Math.Floor(width * .2), centerRight = (int)Math.Ceiling(width * .8);
        int minimumRowPixels = width >= 64 ? 2 : 1;
        int footRow = -1;
        for (int y = bottom; y >= top + (bottom - top) / 2; y--)
        {
            int count = 0;
            for (int x = centerLeft; x < centerRight; x++)
                if (pixels[y * stride + x * 4 + 3] >= VisibleAlpha && ++count >= minimumRowPixels) break;
            if (count >= minimumRowPixels) { footRow = y; break; }
        }
        if (footRow < 0) { footRow = bottom; centerLeft = left; centerRight = right + 1; }
        int footLeft = width, footRight = -1;
        int bandHeight = Math.Max(1, (int)Math.Ceiling(height * .01));
        for (int y = Math.Max(top, footRow - bandHeight + 1); y <= footRow; y++)
        for (int x = centerLeft; x < centerRight; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < VisibleAlpha) continue;
            footLeft = Math.Min(footLeft, x); footRight = Math.Max(footRight, x);
        }
        double footX = footRight >= footLeft ? (footLeft + footRight + 1) / 2.0 : (left + right + 1) / 2.0;
        return new Measurement(new Point(footX / width, (footRow + 1.0) / height),
            new Rect(left / (double)width, top / (double)height,
                (right - left + 1.0) / width, (bottom - top + 1.0) / height));
    }
}
