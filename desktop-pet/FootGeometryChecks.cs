using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

internal static class FootGeometryChecks
{
    public static object Run(string? skinsDirectory = null)
    {
        var checks = new List<string>();
        void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Foot geometry check failed: " + label);
            checks.Add(label);
        }
        const int size = 100;
        var pixels = new byte[size * size * 4];
        void Fill(int x0, int y0, int x1, int y1, byte alpha)
        {
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) pixels[(y * size + x) * 4 + 3] = alpha;
        }
        Fill(30, 10, 70, 70, 255); // body
        Fill(35, 70, 45, 80, 255); Fill(55, 70, 65, 80, 255); // soles
        Fill(2, 85, 8, 95, 255); // low corner ornament, not a support point
        Fill(0, 99, 100, 100, 7); // almost invisible export residue
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        Point foot = FootGeometry.GetPngFoot(bitmap);
        Rect bounds = FootGeometry.GetPngVisibleBounds(bitmap);
        Require(Close(foot.X, .5) && Close(foot.Y, .8), "actual central soles determine support, not corner ornament or transparent canvas");
        Require(Close(bounds.Left, .02) && Close(bounds.Top, .1) && Close(bounds.Right, .7) && Close(bounds.Bottom, .95),
            "visible collision bounds retain ornaments but ignore very faint export residue");
        var fitted = FootGeometry.FitVisibleGeometry(new Size(100, 100), bounds, foot, new Rect(12, 46, 200, 206));
        Require(Close(fitted.Foot.X, 112) && Close(fitted.Foot.Y, 209), "uniform layout includes character origin and centered letterboxing");
        Require(Close(fitted.Bounds.X, 16) && Close(fitted.Bounds.Bottom, 239), "visible rectangle uses same layout transform as foot");
        var wide = FootGeometry.FitVisibleGeometry(new Size(200, 100), new Rect(0, 0, 1, 1), new Point(.5, 1), new Rect(5, 10, 100, 100));
        Require(Close(wide.Foot.Y, 85) && Close(wide.Bounds.Top, 35), "wide source handles top and bottom letterboxing");
        var tall = FootGeometry.FitVisibleGeometry(new Size(100, 200), new Rect(0, 0, 1, 1), new Point(.5, 1), new Rect(5, 10, 100, 100));
        Require(Close(tall.Foot.X, 55) && Close(tall.Bounds.Left, 30) && Close(tall.Foot.Y, 110), "tall source handles horizontal letterboxing");
        Require(FootGeometry.GetPngFoot(bitmap) == foot && FootGeometry.GetPngVisibleBounds(bitmap) == bounds,
            "repeated immutable bitmap measurements remain stable");

        var measuredSkins = new List<object>();
        string directory = skinsDirectory ?? Path.Combine(AppContext.BaseDirectory, "assets", "skins");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string[] files = Directory.GetFiles(directory, "*.png");
        Require(files.Length == 9, "all nine installed PNG skins are covered");
        foreach (string file in files)
        {
            Point actualFoot = FootGeometry.GetPngFoot(file);
            Rect actualBounds = FootGeometry.GetPngVisibleBounds(file);
            Require(actualFoot.X > .3 && actualFoot.X < .7 && actualFoot.Y > .8 && actualFoot.Y < .99 &&
                actualFoot.X >= actualBounds.Left && actualFoot.X <= actualBounds.Right && actualFoot.Y <= actualBounds.Bottom + .000001,
                Path.GetFileName(file) + " has a central visible foot with padding excluded");
            if (string.Equals(Path.GetFileNameWithoutExtension(file), "deepseek", StringComparison.OrdinalIgnoreCase))
            {
                Require(Close(actualFoot.Y, 1135.0 / 1254) && Math.Abs(actualFoot.X - .474) < .005,
                    "DeepSeek sole is at source y=1135, excluding 119 transparent pixels");
                var current = FootGeometry.FitVisibleGeometry(new Size(1254, 1254), actualBounds, actualFoot, new Rect(12, 46, 440, 446));
                Require(Math.Abs(current.Foot.Y - (49 + 440 * 1135.0 / 1254)) < .001,
                    "440 DIP DeepSeek removes 44.75 DIP container-to-sole gap");
            }
            measuredSkins.Add(new { skin = Path.GetFileNameWithoutExtension(file), footX = actualFoot.X, footY = actualFoot.Y,
                visibleLeft = actualBounds.Left, visibleTop = actualBounds.Top, visibleRight = actualBounds.Right, visibleBottom = actualBounds.Bottom });
        }
        return new { passed = true, checks, skins = measuredSkins };
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) < .000001;
}
