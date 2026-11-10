using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.ScreenUnderstanding;

internal sealed record ScreenCaptureRequest(string App, string WindowKey, bool IncludeText,
    bool IncludeImage, bool AllowFullscreen, IReadOnlyList<string> ExcludedApps);

// Raw text and pixels are transient. The identity is a local hash, never a path,
// and must not be inserted into the model prompt or the ordinary chat database.
internal sealed record ScreenCaptureResult(bool Success, string Status, string Text,
    string? ImageBase64, string WindowKey, string ApplicationIdentity)
{
    internal static ScreenCaptureResult Unavailable(string status) => new(false, status, "", null, "", "");
}

internal interface IScreenWindowCapture
{
    Task<ScreenCaptureResult> CaptureAsync(ScreenCaptureRequest request, CancellationToken ct);
    Task<string?> ReadIdentityAsync(string app, string windowKey, CancellationToken ct);
}
