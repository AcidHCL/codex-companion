using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet;

// Runs only in --live2d-test, in the existing non-persisting smoke-test instance.
internal static class Live2DIntegrationChecks
{
    public static async Task<object> RunAsync(PetWindow pet, string? explicitModelPath = null)
    {
        Live2DModelInfo model;
        if (explicitModelPath != null)
            // A failed explicit path must never fall back to the official sample.
            model = new Live2DModelCatalog().Validate(explicitModelPath, "test-explicit");
        else
        {
            var models = Live2DLibrary.Load(out var issues);
            model = models.FirstOrDefault(m => m.Id == "sample-hiyori")
                ?? throw new InvalidOperationException("验收需要随包附带的 Hiyori 官方示例模型。" + string.Join("; ", issues));
        }
        Require(await pet.SelectLive2DAsync(model), pet.Live2DStatus);
        Require(pet.CurrentLive2DModel?.Id == model.Id && pet.Live2DRenderer?.ModelLoaded == true, "Live2D 未成为活动皮肤。");
        await Task.Delay(600);
        string diagnostics = await pet.Live2DRenderer!.GetDiagnosticsAsync();
        string decoded = JsonSerializer.Deserialize<string>(diagnostics) ?? throw new InvalidOperationException("Live2D 诊断为空。");
        using var parsed = JsonDocument.Parse(decoded);
        var renderData = parsed.RootElement.Clone();
        string canvasPath = Path.Combine(AppContext.BaseDirectory, "live2d-canvas-preview.png");
        await pet.Live2DRenderer.CaptureAsync(canvasPath);
        using var canvas = new System.Drawing.Bitmap(canvasPath);
        int transparent = 0, visible = 0;
        for (int y = 0; y < canvas.Height; y += Math.Max(1, canvas.Height / 120))
            for (int x = 0; x < canvas.Width; x += Math.Max(1, canvas.Width / 120))
            {
                byte alpha = canvas.GetPixel(x, y).A;
                if (alpha == 0) transparent++;
                if (alpha > 200) visible++;
            }
        Require(transparent > 50 && visible > 50, "真实 Live2D 画布缺少透明背景或可见模型。");
        pet.SavePreview(Path.Combine(AppContext.BaseDirectory, "live2d-pet-preview.png"));
        using var composite = new System.Drawing.Bitmap(Path.Combine(AppContext.BaseDirectory, "live2d-pet-preview.png"));
        int modelPixels = 0;
        // Character occupies the central square; exclude the bottom status badge and top speech.
        for (int y = 65; y < composite.Height - 55; y += 3)
            for (int x = 20; x < composite.Width - 20; x += 3)
                if (composite.GetPixel(x, y).A > 200) modelPixels++;
        Require(modelPixels > 50, "Live2D 未合成进实际 WPF 桌宠。");
        var library = new Live2DLibraryWindow(pet);
        try
        {
            library.Show();
            await Task.Delay(200);
            library.UpdateLayout();
            var preview = new RenderTargetBitmap((int)Math.Ceiling(library.ActualWidth), (int)Math.Ceiling(library.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            preview.Render(library);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "live2d-library-preview.png"));
            encoder.Save(output);
        }
        finally { library.Close(); }
        var native = SmokeChecks.VerifyWindow(pet);
        await Task.Delay(150);
        Require(pet.CurrentLive2DModel?.Id == model.Id, "窗口缩放或隐藏后丢失模型。");

        pet.SelectSkin("deepseek");
        Require(pet.Live2DRenderer == null && pet.Settings.Live2DModelId == null, "切回 PNG 没有释放 Live2D。");
        var pending = pet.SelectLive2DAsync(model);
        pet.SelectSkin("gemini");
        Require(!await pending && pet.CurrentLive2DModel == null && pet.CurrentSkin.Id == "gemini", "异步加载覆盖了用户的新选择。");

        pet.Hide();
        Require(await pet.SelectLive2DAsync(model), "隐藏时重新加载模型失败：" + pet.Live2DStatus);
        pet.Show();
        await Task.Delay(450);
        var shownGeometry = await pet.Live2DRenderer!.CaptureContactGeometryAsync();
        var host = (FrameworkElement)pet.Live2DRenderer.Parent;
        var shownFootDip = host.TransformToAncestor(pet).Transform(new Point(shownGeometry.Foot.X * host.ActualWidth, shownGeometry.Foot.Y * host.ActualHeight));
        double shownDpi = NativeMethods.GetDpiScale(new System.Windows.Interop.WindowInteropHelper(pet).Handle);
        double shownFootError = (pet.VisibleFootPixels - new Point(shownFootDip.X * shownDpi, shownFootDip.Y * shownDpi)).Length;
        Require(shownFootError <= 2, "隐藏时加载的模型在显示后没有重新测量可见脚点。");
        var missing = model with { ModelPath = Path.Combine(model.RootDirectory, "missing-test.model3.json") };
        Require(!await pet.SelectLive2DAsync(missing), "缺失模型被误报为载入成功。");
        Require(pet.Live2DRenderer == null && pet.CurrentLive2DModel == null && pet.Settings.Live2DModelId == null, "模型失败后未恢复 PNG。");
        pet.SelectSkin("deepseek");
        return new
        {
            passed = true, model = model.Name, modelPath = model.ModelPath,
            modelSource = explicitModelPath == null ? "official-sample-hiyori" : "explicit-model", renderer = renderData,
            sampledTransparentPixels = transparent, sampledVisiblePixels = visible, wpfModelPixels = modelPixels,
            native, pngRestored = true, rapidSwitchProtected = true, reloadPassed = true, missingModelFallback = true,
            hiddenLoadFootRecovered = true, hiddenLoadFootErrorPixels = shownFootError,
            libraryWindowRendered = true, settingsPersisted = false
        };
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
