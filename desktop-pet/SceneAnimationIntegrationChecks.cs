using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopPet;

// Explicit --scene-test only. Geometry/video fixtures are never installed as pet skins.
internal static class SceneAnimationIntegrationChecks
{
    public static async Task<object> RunAsync(PetWindow pet, string manifestPath)
    {
        var pack = new SceneAnimationCatalog().Validate(manifestPath, "scene-renderer-test");
        Require(pack.Name == "Renderer verification geometry", "请选择 scene-web/fixtures/verification.petanim.json 测试清单。");
        pet.SetSceneAuto(false);
        string? originalModel = pet.CurrentLive2DModel?.ModelPath;
        var originalRenderer = pet.Live2DRenderer;
        Require(await pet.SelectScenePackAsync(pack), pet.SceneStatus);
        var renderer = pet.SceneRenderer ?? throw new InvalidOperationException("场景播放器未创建。");
        string output = Path.Combine(AppContext.BaseDirectory, "scene-test-evidence");
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        async Task<JsonElement> Read() => Decode(await renderer.GetDiagnosticsAsync());
        async Task Wait(Func<JsonElement, bool> condition, int timeout = 10000)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeout)
            {
                if (condition(await Read())) return;
                await Task.Delay(35);
            }
            throw new TimeoutException("场景播放器未进入预期状态：" + await renderer.GetDiagnosticsAsync());
        }
        async Task<string> Capture(string name)
        {
            string file = Path.Combine(output, name + ".png"); await renderer.CaptureAsync(file); return file;
        }
        async Task<JsonElement> PauseAtRenderedFrame()
        {
            renderer.SetPaused(true);
            await Wait(data => data.GetProperty("paused").GetBoolean() && !data.GetProperty("timerActive").GetBoolean());
            var state = await Read();
            // A web-message acknowledgement precedes the compositor's presentation.
            // Let the frozen canvas reach CapturePreview, then compare its actual
            // frame index, not the index observed before the asynchronous pause.
            await Task.Delay(70);
            return state;
        }
        async Task Idle()
        {
            pet.StopScenePreview();
            await Wait(data => !data.GetProperty("playing").GetBoolean());
            Require(!pet.IsScenePlaying, "停止动作后场景层仍显示。");
            Require(ReferenceEquals(pet.Live2DRenderer, originalRenderer), "切换场景替换了待机 Live2D。");
            if (originalRenderer != null) Require(originalRenderer.Opacity == 1, "停止动作后未恢复 Live2D 可见性。");
        }
        foreach (string action in new[] { "working", "walk" })
        {
            await pet.PreviewSceneAsync(action);
            Require(pet.IsScenePlaying && renderer.Opacity == 1, "动画第一帧就绪后未显示场景层。");
            if (originalRenderer != null) Require(originalRenderer.Opacity == 0, "动画覆盖时 Live2D 未隐藏。");
            var frozen = await PauseAtRenderedFrame();
            int firstFrame = frozen.GetProperty("frame").GetInt32();
            Require(IsFrameColor(await Capture(action + "-first"), firstFrame), "PNG/精灵图当前帧像素不正确。");
            await Task.Delay(200);
            Require((await Read()).GetProperty("draws").GetInt32() == frozen.GetProperty("draws").GetInt32(), "暂停后仍在绘帧。");
            renderer.SetPaused(false);
            await Wait(data => data.GetProperty("frame").ValueKind == JsonValueKind.Number && data.GetProperty("frame").GetInt32() != firstFrame);
            var changed = await PauseAtRenderedFrame();
            int changedFrame = changed.GetProperty("frame").GetInt32();
            Require(changedFrame != firstFrame && IsFrameColor(await Capture(action + "-changed"), changedFrame), "PNG/精灵图没有显示实际变化后的帧。");
            renderer.SetPaused(false);
            if (action == "walk")
            {
                await Wait(data => data.GetProperty("frame").ValueKind == JsonValueKind.Number && data.GetProperty("frame").GetInt32() >= 2);
                var secondRow = await PauseAtRenderedFrame(); int secondRowFrame = secondRow.GetProperty("frame").GetInt32();
                Require(secondRowFrame >= 2 && IsFrameColor(await Capture(action + "-second-row"), secondRowFrame), "精灵图第二行坐标错误。"); renderer.SetPaused(false);
            }
            checks.Add(new { action, pixels = true, pause = true }); await Idle();
        }
        await pet.PreviewSceneAsync("complete");
        await Wait(data => data.GetProperty("frame").ValueKind == JsonValueKind.Number && data.GetProperty("frame").GetInt32() == 3);
        await PauseAtRenderedFrame();
        string apng = await Capture("apng-disposal");
        using (var bitmap = new System.Drawing.Bitmap(apng))
        {
            int side = Math.Min(bitmap.Width, bitmap.Height), left = (bitmap.Width - side) / 2, top = bitmap.Height - side;
            var cleared = bitmap.GetPixel(left + side * 3 / 4, top + side / 8);
            var yellow = bitmap.GetPixel(left + side / 8, top + side * 3 / 4);
            Require(cleared.A == 0 && yellow.R > 240 && yellow.G > 240 && yellow.B < 10, "APNG 后续帧/背景处置未正确合成。");
        }
        renderer.SetPaused(false); await Wait(data => !data.GetProperty("playing").GetBoolean());
        Require(!pet.IsScenePlaying, "单次动画完成未返回待机。"); checks.Add(new { action = "complete", apngAnimated = true, disposal = true, returnedIdle = true });
        foreach (string action in new[] { "drag", "fall" })
        {
            await pet.PreviewSceneAsync(action);
            await Wait(data => data.GetProperty("videoTime").GetDouble() > .15);
            var state = await PauseAtRenderedFrame();
            await Task.Delay(200); var still = await Read();
            Require(still.GetProperty("videoTime").GetDouble() == state.GetProperty("videoTime").GetDouble(), "视频暂停无效。");
            Require(state.GetProperty("muted").GetBoolean(), "视频未强制静音。");
            string videoFile = action == "drag" ? "bear-vp8a.webm" : "bear.mp4";
            byte[] prefix = new byte[64];
            using (var input = File.OpenRead(Path.Combine(pack.RootDirectory, videoFile))) input.ReadExactly(prefix);
            int rangeBefore = renderer.RangeRequests;
            var browser = (WebView2CompositionControl)renderer.Children[0];
            // Parallel HTTP range reads must retain independent cursors over one
            // shared safe file handle. Compare their bodies with the original bytes.
            string script = "(async()=>{const expected=" + JsonSerializer.Serialize(prefix) + ";const bytes=Uint8Array.from(atob(expected),c=>c.charCodeAt(0));"
                + "const checks=await Promise.all(Array.from({length:40},async(_,i)=>{const start=i%32;const r=await fetch('/media/" + videoFile
                + "',{headers:{Range:'bytes='+start+'-'+(start+15)},cache:'no-store'});const actual=new Uint8Array(await r.arrayBuffer());return r.status===206&&actual.length===16&&actual.every((v,j)=>v===bytes[start+j]);}));return checks.every(Boolean);})()";
            // ExecuteScriptAsync does not await promises; store a test-only completion
            // value in this renderer instance and poll it from the WPF dispatcher.
            await browser.ExecuteScriptAsync("window.__sceneRangeCheck=null;" + script + ".then(value=>window.__sceneRangeCheck=value).catch(()=>window.__sceneRangeCheck=false)");
            var rangeWatch = Stopwatch.StartNew(); string rangeResult = "null";
            while (rangeWatch.ElapsedMilliseconds < 10000 && rangeResult == "null")
            {
                await Task.Delay(35); rangeResult = await browser.ExecuteScriptAsync("window.__sceneRangeCheck");
            }
            Require(rangeResult == "true" && renderer.RangeRequests - rangeBefore >= 40, "并发视频 Range 请求返回了错误片段。");
            Require(renderer.OpenVideoSources == 1, "重复 Range 请求增长了视频文件句柄数量。");
            string file = await Capture(action == "drag" ? "webm-alpha" : "mp4");
            using var bitmap = new System.Drawing.Bitmap(file);
            int partial = 0, visible = 0;
            for (int y = 0; y < bitmap.Height; y += 3) for (int x = 0; x < bitmap.Width; x += 3)
            {
                byte alpha = bitmap.GetPixel(x, y).A; if (alpha is > 0 and < 250) partial++; if (alpha > 0) visible++;
            }
            Require(visible > 100, "视频未显示实际画面。");
            if (action == "drag") Require(partial > 100, "透明 WebM 的 alpha 丢失。");
            checks.Add(new { action, decodedVisiblePixels = visible, partialAlphaPixels = partial, muted = true, pause = true, concurrentRanges = 40, sharedVideoSources = renderer.OpenVideoSources });
            renderer.SetPaused(false); await Idle();
            Require(renderer.OpenVideoSources == 0, "停止视频后共享源没有释放。");
            await browser.ExecuteScriptAsync("window.__lateSceneRange=null;fetch('/media/" + videoFile + "',{headers:{Range:'bytes=0-15'},cache:'no-store'}).then(r=>window.__lateSceneRange=r.status).catch(()=>window.__lateSceneRange=-1)");
            var lateWatch = Stopwatch.StartNew(); string lateStatus = "null";
            while (lateWatch.ElapsedMilliseconds < 5000 && lateStatus == "null")
            {
                await Task.Delay(35); lateStatus = await browser.ExecuteScriptAsync("window.__lateSceneRange");
            }
            Require(lateStatus == "410" && renderer.OpenVideoSources == 0, "迟到的视频请求在停止后重新打开了文件。");
            using (var unlocked = File.Open(Path.Combine(pack.RootDirectory, action == "drag" ? "bear-vp8a.webm" : "bear.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(unlocked.Length > 0, "停止视频后资源文件仍被播放器锁定。");
        }
        Require(renderer.RangeRequests > 0, "视频未经过原生 Range 资源响应。");
        await pet.PreviewSceneAsync("jump"); await Wait(data => !data.GetProperty("playing").GetBoolean());
        Require(!pet.IsScenePlaying, "start/loop/end 单轮预览没有返回待机。");
        checks.Add(new { action = "jump", startLoopEnd = true });

        var activeSnapshot = CodexSnapshot.Empty with
        {
            Connected = true,
            Tasks = new[] { new CodexTaskInfo("scene-check", "场景状态测试", "active", "codex", null, null, 1, false) }
        };
        pet.SetCodexStatus(activeSnapshot); pet.SetSceneAuto(true);
        await Wait(data => data.GetProperty("playing").GetBoolean() && data.GetProperty("kind").GetString() == "frames");
        string? workingRequest = (await Read()).GetProperty("requestId").GetString();
        pet.SetCodexStatus(activeSnapshot); pet.SetCodexStatus(activeSnapshot); await Task.Delay(180);
        Require((await Read()).GetProperty("requestId").GetString() == workingRequest, "重复任务轮询重启了工作动画。");
        pet.SetSceneAuto(false); await Wait(data => !data.GetProperty("playing").GetBoolean());
        Require(!pet.IsScenePlaying, "关闭自动场景未恢复待机。");
        pet.SetSceneAuto(true); await Wait(data => data.GetProperty("playing").GetBoolean());
        await pet.PreviewSceneAsync("complete");
        await Wait(data => data.GetProperty("kind").ValueKind == JsonValueKind.String && data.GetProperty("kind").GetString() == "apng");
        await Wait(data => data.GetProperty("kind").ValueKind == JsonValueKind.String && data.GetProperty("kind").GetString() == "frames");
        Require(pet.IsScenePlaying, "手动预览完成后没有恢复工作动画。");
        pet.Hide(); await Task.Delay(120);
        var hidden = await Read(); await Task.Delay(180); var hiddenLater = await Read();
        Require(hidden.GetProperty("paused").GetBoolean() && !hidden.GetProperty("timerActive").GetBoolean()
            && hidden.GetProperty("draws").GetInt32() == hiddenLater.GetProperty("draws").GetInt32(), "隐藏桌宠后仍在绘制动画。");
        pet.Show(); await Wait(data => !data.GetProperty("paused").GetBoolean() && data.GetProperty("draws").GetInt32() > hiddenLater.GetProperty("draws").GetInt32());
        pet.OnVerifiedSceneNotice(new PetNotice("已完成", "场景验收中的已验证完成事件", "complete"));
        await Wait(data => data.GetProperty("kind").ValueKind == JsonValueKind.String && data.GetProperty("kind").GetString() == "apng");
        await Wait(data => data.GetProperty("kind").ValueKind == JsonValueKind.String && data.GetProperty("kind").GetString() == "frames");
        checks.Add(new { automaticWorking = true, repeatedPollDoesNotRestart = true, disableReturnsIdle = true,
            previewReturnsWorking = true, hiddenPauses = true, shownResumes = true, verifiedCompleteReturnsWorking = true });
        pet.SetSceneAuto(false); await Wait(data => !data.GetProperty("playing").GetBoolean());

        // Corrupt only a dedicated generated test fixture after selection, then restore it.
        string errorFile = Path.Combine(pack.RootDirectory, "decoder-error.png");
        byte[] originalBytes = File.ReadAllBytes(errorFile);
        try
        {
            File.WriteAllBytes(errorFile, new byte[] { 1, 2, 3 });
            await pet.PreviewSceneAsync("idle");
            Require(!pet.IsScenePlaying && pet.SceneStatus.Contains("失败", StringComparison.Ordinal), "解码失败未恢复待机或报告失败。");
        }
        finally { File.WriteAllBytes(errorFile, originalBytes); }
        Require(pet.CurrentLive2DModel?.ModelPath == originalModel && ReferenceEquals(pet.Live2DRenderer, originalRenderer), "动画错误覆盖了用户的 Live2D 模型。");
        if (originalRenderer != null) Require(originalRenderer.Opacity == 1, "错误后 Live2D 未恢复显示。");
        checks.Add(new { decoderFailureFallback = true, live2DIdentityPreserved = true, imageFileReleased = true, stoppedVideoFilesReleased = true });
        int ranges = renderer.RangeRequests;
        string libraryPreview = Path.Combine(AppContext.BaseDirectory, "scene-library-preview.png");
        var library = new SceneAnimationLibraryWindow(pet);
        try
        {
            library.Show(); await Task.Delay(200); library.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(library.ActualWidth), (int)Math.Ceiling(library.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(library);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(libraryPreview); encoder.Save(file);
        }
        finally { library.Close(); }
        pet.ClearScenePack();
        if (pet.CurrentLive2DModel == null && pet.CurrentSkin.Id == "deepseek")
        {
            var restore = Stopwatch.StartNew();
            while (restore.ElapsedMilliseconds < 15000 && (!pet.UsesBuiltInScenes || !pet.IsScenePlaying)) await Task.Delay(35);
            Require(pet.UsesBuiltInScenes && pet.IsScenePlaying && !ReferenceEquals(pet.SceneRenderer, renderer), "解除自定义包后没有恢复 DeepSeek 默认动画或释放旧播放器。");
        }
        else Require(pet.SceneRenderer == null && !pet.IsScenePlaying, "清除动作包未释放播放器。");
        return new { passed = true, fixture = manifestPath, live2DModel = originalModel, checks, rangeRequests = ranges, output, libraryPreview, settingsPersisted = false };
    }

    private static JsonElement Decode(string result)
    {
        string json = JsonSerializer.Deserialize<string>(result) ?? throw new InvalidOperationException("场景诊断为空。");
        using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone();
    }
    private static bool IsColor(string file, int r, int g, int b)
    {
        using var bitmap = new System.Drawing.Bitmap(file);
        var color = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        return Math.Abs(color.R - r) < 10 && Math.Abs(color.G - g) < 10 && Math.Abs(color.B - b) < 10 && color.A > 240;
    }
    private static bool IsFrameColor(string file, int frame) => frame switch
    {
        0 => IsColor(file, 255, 0, 0), 1 => IsColor(file, 0, 255, 0),
        2 => IsColor(file, 0, 0, 255), 3 => IsColor(file, 255, 255, 0), _ => false
    };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
