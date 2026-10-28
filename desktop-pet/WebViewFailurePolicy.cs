using System;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace DesktopPet;

internal static class WebViewFailurePolicy
{
    // GPU and utility exits are automatically recovered by WebView2. A transient
    // unresponsive notification (for example during resume) is not a crash.
    internal static bool RequiresRecreation(CoreWebView2ProcessFailedKind kind) => kind is
        CoreWebView2ProcessFailedKind.BrowserProcessExited or
        CoreWebView2ProcessFailedKind.RenderProcessExited or
        CoreWebView2ProcessFailedKind.FrameRenderProcessExited;

    internal static bool IsUnavailable(Exception error) =>
        !GraphicsFailure.IsFatal(error) && (error is COMException or InvalidOperationException);
}
