using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenCapturePolicy
{
    internal const int MaximumText = 4000;
    internal const int MaximumImageBytes = 1024 * 1024;
    internal const int MaximumImageEdge = 1280;
    internal const long MaximumCapturePixels = 16_000_000;
    // Used only for our UTF-8 stdin/stdout JSON pipe, never embedded in HTML.
    // Avoid expanding base64 '+' into six characters and invalidating the
    // bounded one-mebibyte image transport budget.
    internal static readonly JsonSerializerOptions WireOptions = new()
    { MaxDepth = 8, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static bool ValidRequest(ScreenCaptureRequest? request) => request != null &&
        (request.IncludeText || request.IncludeImage) && ValidApp(request.App) &&
        TryWindowKey(request.WindowKey, out _, out _) && request.ExcludedApps != null &&
        request.ExcludedApps.Count <= 64 && request.ExcludedApps.All(ValidApp);

    internal static bool ValidApp(string? app) => !string.IsNullOrWhiteSpace(app) && app.Length <= 80 &&
        !app.Any(character => char.IsControl(character) || character is '\\' or '/' or ':' or '*' or '?' or '<' or '>' or '|' or '"');

    internal static string NormalizeApp(string app) => (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        ? app[..^4] : app).Trim().ToLowerInvariant();

    internal static bool TryWindowKey(string? key, out uint pid, out IntPtr window)
    {
        pid = 0; window = IntPtr.Zero;
        if (key == null || key.Length is < 3 or > 64) return false;
        string[] parts = key.Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid == 0 ||
            !long.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long handle) || handle <= 0) return false;
        window = new IntPtr(handle); return true;
    }

    internal static bool ValidResult(ScreenCaptureResult? result, ScreenCaptureRequest request)
    {
        if (result == null || result.Status == null || result.Text == null || result.WindowKey == null || result.ApplicationIdentity == null ||
            result.Text.Length > MaximumText || result.Status.Length > 64 ||
            result.Status.Any(character => !(character is >= 'a' and <= 'z' or '_'))) return false;
        if (!result.Success) return result.Text.Length == 0 && result.ImageBase64 == null && result.WindowKey.Length == 0 && result.ApplicationIdentity.Length == 0;
        if (!string.Equals(result.WindowKey, request.WindowKey, StringComparison.OrdinalIgnoreCase) ||
            result.ApplicationIdentity.Length != 64 || result.ApplicationIdentity.Any(character => !Uri.IsHexDigit(character)) ||
            !request.IncludeText && result.Text.Length != 0 || !request.IncludeImage && result.ImageBase64 != null) return false;
        if (result.ImageBase64 != null)
        {
            if (result.ImageBase64.Length > (MaximumImageBytes + 2) / 3 * 4) return false;
            try
            {
                byte[] image = Convert.FromBase64String(result.ImageBase64);
                if (image.Length is < 4 or > MaximumImageBytes || image[0] != 255 || image[1] != 216 || image[2] != 255) return false;
            }
            catch (FormatException) { return false; }
        }
        return result.Text.Length > 0 || result.ImageBase64 != null;
    }

    internal static string BoundText(string value, int maximum = MaximumText)
    {
        if (maximum <= 0) return "";
        var text = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (char character in value)
        {
            if (char.IsControl(character) && character is not ('\n' or '\t')) continue;
            text.Append(character);
            if (text.Length >= maximum) break;
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return text.ToString().Trim();
    }
}
