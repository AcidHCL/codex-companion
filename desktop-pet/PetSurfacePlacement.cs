using System;
using System.Windows;

namespace DesktopPet;

internal static class PetSurfacePlacement
{
    // Inputs and result use virtual-desktop pixels, including negative origins.
    internal static Rect Place(Rect characterScreenPixels, Rect workPixels, Size requestedPixelSize,
        double gap = 12, bool centerVertically = false)
    {
        double width = Math.Min(requestedPixelSize.Width, workPixels.Width);
        double height = Math.Min(requestedPixelSize.Height, workPixels.Height);
        double leftRoom = characterScreenPixels.Left - workPixels.Left - gap;
        double rightRoom = workPixels.Right - characterScreenPixels.Right - gap;
        double x = characterScreenPixels.Left - width - gap;
        double y = centerVertically ? characterScreenPixels.Top + (characterScreenPixels.Height - height) / 2 : characterScreenPixels.Bottom - height;
        if (leftRoom < width)
        {
            if (rightRoom >= width) x = characterScreenPixels.Right + gap;
            else if (characterScreenPixels.Top - workPixels.Top >= height + gap)
            {
                x = characterScreenPixels.Left + (characterScreenPixels.Width - width) / 2;
                y = characterScreenPixels.Top - height - gap;
            }
            else if (workPixels.Bottom - characterScreenPixels.Bottom >= height + gap)
            {
                x = characterScreenPixels.Left + (characterScreenPixels.Width - width) / 2;
                y = characterScreenPixels.Bottom + gap;
            }
            else if (rightRoom > leftRoom) x = characterScreenPixels.Right + gap;
        }
        return new Rect(Math.Clamp(x, workPixels.Left, workPixels.Right - width),
            Math.Clamp(y, workPixels.Top, workPixels.Bottom - height), width, height);
    }
}
