using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.ScreenUnderstanding;

internal static class ScreenCaptureWorker
{
    // Route before desktop relaunch/settings/logging. No WPF Application, tray,
    // pairing, voice backend or model client is constructed in this process.
    internal static int Run()
    {
        ScreenCaptureResult result;
        try
        {
            using var inputDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, false, 4096, leaveOpen: true);
            string json = WindowsScreenWindowCapture.ReadBoundedAsync(input,
                WindowsScreenWindowCapture.MaximumRequestCharacters, inputDeadline.Token).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<ScreenCaptureRequest>(json, new JsonSerializerOptions { MaxDepth = 8 });
            if (request == null || !ScreenCapturePolicy.ValidRequest(request)) result = ScreenCaptureResult.Unavailable("invalid_request");
            else
            {
                // Task.Run is MTA; never query another process's accessibility
                // provider on the STA entry point. The whole worker is bounded.
                var capture = Task.Run(() => ScreenCaptureNative.Capture(request));
                result = capture.Wait(TimeSpan.FromSeconds(6)) ? capture.GetAwaiter().GetResult()
                    : ScreenCaptureResult.Unavailable("capture_timeout");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { result = ScreenCaptureResult.Unavailable("capture_unavailable"); }
        try
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 4096, leaveOpen: true);
            output.Write(JsonSerializer.Serialize(result, ScreenCapturePolicy.WireOptions)); output.Flush();
            return 0;
        }
        catch { return 1; }
    }
}
