using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace DesktopPet;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Normalize the process before GraphicsRecovery, settings, pairing, or
        // logging touches AppData. A Desktop Bridge child can see redirected
        // files despite reporting no package identity of its own.
        try
        {
            bool launchFixture = args.Any(value => value is "--independent-launch-test" or "--independent-launch-host" or "--independent-launch-child");
            if (launchFixture && IndependentLaunchChecks.HandleProbe(args) is int fixtureResult) return fixtureResult;
            bool usesRealConnection = StartupModePolicy.UsesRealConnection(args);
            bool isolatedDiagnostic = StartupModePolicy.IsIsolatedDiagnostic(args);
            if ((usesRealConnection || !isolatedDiagnostic) && !args.Contains("--exit", StringComparer.Ordinal))
                if (IndependentProcessLauncher.RelaunchIfNeeded(args) is int relayResult) return relayResult;
            if (args.Contains("--desktop-environment-test", StringComparer.Ordinal) && IndependentLaunchChecks.HandleProbe(args) is int environmentResult) return environmentResult;
        }
        catch (Exception ex)
        {
            // The desktop context is not verified yet: do not create any user
            // data here. This report contains no caller context or arguments.
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "independent-start-validation.json"), JsonSerializer.Serialize(new { passed = false, errorType = ex.GetType().Name, errorHResult = ex.HResult })); } catch { }
            if (!args.Any(value => value.EndsWith("-test", StringComparison.Ordinal) || value is "--pair-codex" or "--independent-launch-host" or "--independent-launch-child"))
                MessageBox.Show("桌宠无法进入独立桌面环境。请从解压目录直接启动 DesktopPet.exe；无需管理员权限。", AppVersion.ProductLabel, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        if (!GraphicsRecovery.Initialize(args)) return 2;
        if (GraphicsRecovery.CompatibilityMode) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        if (DisplayRecoveryIntegrationChecks.HandleProbe(args) is int probeResult) return probeResult;
        if (args.Contains("--movement-fixture")) return MovementRegressionChecks.RunFixture(args);
        bool selfTest = args.Contains("--self-test");
        bool integration = args.Contains("--integration-test");
        bool live2DTest = args.Contains("--live2d-test");
        bool sceneTest = args.Contains("--scene-test");
        bool movementTest = args.Contains("--movement-test");
        bool deepSeekStateTest = args.Contains("--deepseek-state-test");
        bool fallLandTest = args.Contains("--fall-land-test");
        bool dragStateTest = args.Contains("--drag-state-test");
        bool smoke = args.Contains("--smoke-test") || integration || live2DTest || sceneTest || movementTest || deepSeekStateTest || fallLandTest || dragStateTest;
        if (smoke) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), "{\"passed\":false,\"status\":\"starting\"}");
        try
        {
            if (args.Contains("--speech-pcm-live-test")) return PetPcmLiveChecks.Run();
            if (args.Contains("--speech-stream-live-test")) return PetStreamingSpeechLiveChecks.Run();
            if (args.Contains("--voice-worker-lifecycle-test")) return PetVoiceWorkerChecks.Run();
            if (args.Contains("--streaming-text-test")) return PetStreamingTextChecks.Run();
            if (args.Contains("--speech-test")) return PetSpeechChecks.Run();
            if (args.Contains("--speech-ui-test")) return SpeechUiChecks.Run();
            if (args.Contains("--speech-live-test")) return PetSpeechLiveChecks.Run();
            if (args.Contains("--voice-input-test")) return PetVoiceInputChecks.Run();
            if (args.Contains("--voice-input-ui-test")) return VoiceInputUiChecks.Run();
            if (args.Contains("--voice-input-live-test")) return PetVoiceInputChecks.RunLive();
            if (args.Contains("--microphone-test")) return PetMicrophoneChecks.Run();
            if (args.Contains("--ollama-client-test")) return RunOllamaClientCheck().GetAwaiter().GetResult();
            if (args.Contains("--chat-store-test") || args.Contains("--shared-memory-test")) return RunChatMemoryCheck(args.Contains("--chat-store-test"));
            if (args.Contains("--chat-history-ui-test")) return ChatHistoryUiChecks.Run();
            if (args.Contains("--chat-memory-live-test")) return ChatMemoryLiveChecks.Run();
            if (args.Contains("--pet-behavior-planner-test")) return RunBehaviorPlannerCheck().GetAwaiter().GetResult();
            if (args.Contains("--pet-behavior-queue-test")) return RunBehaviorQueueCheck();
            if (args.Contains("--pet-click-interaction-test")) return PetClickInteractionChecks.Run();
            if (args.Contains("--pet-click-pointer-fixture")) return PetClickInteractionChecks.Run(pointerFixture: true);
            if (args.Contains("--pet-behavior-motion-test")) return PetBehaviorMotionChecks.Run();
            if (args.Contains("--deepseek-motion-test")) return DeepSeekMotionIntegrationChecks.Run();
            if (args.Contains("--redrawn-motion-test")) return DeepSeekMotionIntegrationChecks.Run(true);
            if (args.Contains("--redrawn-animation-test")) return RedrawnAnimationIntegrationChecks.Run();
            if (args.Contains("--pet-behavior-chat-test")) return PetBehaviorChatChecks.Run();
            if (args.Contains("--pet-behavior-coordinator-test")) return PetBehaviorCoordinatorChecks.Run();
            if (args.Contains("--pet-behavior-live-test")) return RunBehaviorLiveCheck().GetAwaiter().GetResult();
            if (args.Contains("--web-client-test") || args.Contains("--web-chat-test")) return RunWebCheck(args.Contains("--web-client-test")).GetAwaiter().GetResult();
            if (args.Contains("--web-ui-test") || args.Contains("--web-live-test")) return PetWebUiChecks.Run(args.Contains("--web-live-test"));
            if (args.Contains("--pet-companion-ui-test")) return PetCompanionUiChecks.Run();
            if (args.Contains("--ollama-chat-test") || args.Contains("--ollama-live-test")) return OllamaChatChecks.Run(args.Contains("--ollama-live-test"));
            if (args.Contains("--monitor-recovery-test")) return RunMonitorRecoveryCheck().GetAwaiter().GetResult();
            if (args.Contains("--lifecycle-log-test")) return RunLifecycleLogCheck();
            if (args.Contains("--pairing-recovery-test")) return RunPairingRecoveryCheck().GetAwaiter().GetResult();
            if (args.Contains("--pairing-migration-test")) return RunPairingMigrationCheck();
            if (args.Contains("--version-ui-test")) return VersionUiChecks.Run();
            if (args.Contains("--topmost-test")) return TopmostIntegrationChecks.Run();
            if (args.Contains("--display-recovery-test")) return DisplayRecoveryIntegrationChecks.Run();
            if (args.Contains("--remote-session-test")) return RemoteSessionIntegrationChecks.Run();
            if (args.Contains("--task-source-test")) return TaskSourceIntegrationChecks.Run();
            if (args.Contains("--privacy-ui-test")) return CodexPrivacyUiChecks.Run();
            if (args.Contains("--scene-audio-ui-test")) return SceneAudioUiChecks.Run();
            if (args.Contains("--security-test") || args.Contains("--pipe-trust-test")) return RunSecurityCheck(args.Contains("--pipe-trust-test")).GetAwaiter().GetResult();
            if (args.Contains("--connection-test") || args.Contains("--pair-codex")) return RunConnectionCheck(args.Contains("--pair-codex")).GetAwaiter().GetResult();
            int modelOption = Array.IndexOf(args, "--live2d-test-model");
            string? live2DTestModel = null;
            if (modelOption >= 0)
            {
                if (!live2DTest || modelOption + 1 >= args.Length || args[modelOption + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--live2d-test-model 需要同时指定 --live2d-test 和模型入口路径。");
                live2DTestModel = Path.GetFullPath(args[modelOption + 1]);
            }
            var skins = SkinCatalog.Load(AppContext.BaseDirectory);
            int sceneOption = Array.IndexOf(args, "--scene-test-manifest");
            string? sceneManifest = null;
            if (sceneTest)
            {
                if (sceneOption < 0 || sceneOption + 1 >= args.Length || args[sceneOption + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--scene-test 需要 --scene-test-manifest 和本地测试清单路径。");
                sceneManifest = Path.GetFullPath(args[sceneOption + 1]);
            }
            if (selfTest) return ValidateAssets(skins);
            using var mutex = new Mutex(true, @"Local\AIPet.DesktopPet", out bool owner);
            if (!owner)
            {
                if (smoke) throw new InvalidOperationException("测试未运行：请先退出已经运行的桌宠。");
                try { using var signal = EventWaitHandle.OpenExisting(args.Contains("--exit") ? @"Local\AIPet.Exit" : args.Contains("--chat") ? @"Local\AIPet.ShowChat" : @"Local\AIPet.ShowSettings"); signal.Set(); } catch { }
                return 0;
            }
            if (args.Contains("--exit")) return 0;
            if (!smoke)
            {
                LifecycleLog.Start();
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    if (e.ExceptionObject is Exception exception)
                        LifecycleLog.RecordException(LifecycleEvent.DomainUnhandledException, exception);
                    else LifecycleLog.Record(LifecycleEvent.DomainUnhandledException);
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => LifecycleLog.Record(LifecycleEvent.ProcessExit, Environment.ExitCode);
            }
            var app = new PetApplication(skins, smoke, integration, live2DTest, live2DTestModel, sceneManifest, movementTest, deepSeekStateTest, fallLandTest, dragStateTest);
            int result = app.Run();
            mutex.ReleaseMutex();
            return result;
        }
        catch (Exception ex)
        {
            LifecycleLog.RecordException(LifecycleEvent.StartupFailure, ex);
            Log(ex);
            if (selfTest) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "asset-validation.json"), JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            if (smoke) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            if (!selfTest && !smoke) MessageBox.Show("桌宠启动失败。\n" + ex.Message + "\n\n详细信息已写入本地日志。", AppVersion.ProductLabel, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task<int> RunBehaviorPlannerCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "pet-behavior-planner-validation.json");
        try
        {
            object checks = await PetBehaviorPlannerChecks.RunAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, version = AppVersion.Number, error = DiagnosticError(ex), detail = ex.Message })); return 1;
        }
    }

    private static int RunBehaviorQueueCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "pet-behavior-queue-validation.json");
        try
        {
            object checks = PetInteractionQueueChecks.Run();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true })); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, version = AppVersion.Number, error = DiagnosticError(ex), detail = ex.Message })); return 1;
        }
    }

    private static async Task<int> RunBehaviorLiveCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "pet-behavior-live-validation.json");
        try
        {
            object checks = await PetBehaviorLiveChecks.RunAsync();
            var report = JsonSerializer.SerializeToElement(checks);
            bool passed = report.GetProperty("passed").GetBoolean();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, version = AppVersion.Number, error = DiagnosticError(ex), detail = ex.Message })); return 1;
        }
    }

    private static int RunChatMemoryCheck(bool storage)
    {
        string output = Path.Combine(AppContext.BaseDirectory, storage ? "chat-store-validation.json" : "shared-memory-validation.json");
        try
        {
            object checks = storage ? ChatMemoryStoreChecks.Run() : SharedMemoryContextChecks.Run();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, version = AppVersion.Number, error = DiagnosticError(ex), detail = ex.Message }));
            return 1;
        }
    }

    private static async Task<int> RunOllamaClientCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "ollama-client-validation.json");
        try
        {
            object checks = await OllamaClientChecks.RunAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static async Task<int> RunWebCheck(bool client)
    {
        string output = Path.Combine(AppContext.BaseDirectory, client ? "web-client-validation.json" : "web-chat-validation.json");
        try
        {
            object checks = client ? await PetWebClientChecks.RunAsync() : await PetWebChatChecks.RunAsync();
            object? answerContext = client ? null : PetWebAnswerContextChecks.Run();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks, answerContext }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex), detail = ex.Message }));
            return 1;
        }
    }

    private static int RunLifecycleLogCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "lifecycle-log-validation.json");
        try
        {
            object checks = LifecycleLogChecks.Run();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static async Task<int> RunPairingRecoveryCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "pairing-recovery-validation.json");
        try
        {
            object checks = await CodexPairingRecoveryChecks.RunAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static int RunPairingMigrationCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "pairing-migration-validation.json");
        try
        {
            object checks = CodexPairingMigrationChecks.Run(AppContext.BaseDirectory);
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static int ValidateAssets(IReadOnlyList<SkinInfo> skins)
    {
        var parserChecks = CodexParserChecks.Run();
        var live2DImportChecks = Live2DModelCatalogChecks.Run();
        var sceneImportChecks = SceneAnimationCatalogChecks.Run();
        var movementChecks = DesktopMovementChecks.Run();
        var deepSeekMotionChecks = DeepSeekMotionChecks.Run();
        var footGeometryChecks = FootGeometryChecks.Run(Path.Combine(AppContext.BaseDirectory, "assets", "skins"));
        var sceneTriggerChecks = SceneTriggerPolicy.RunChecks();
        var companionTaskChecks = CompanionTaskRouterChecks.Run();
        var deepSeekStateChecks = DeepSeekAnimationState.RunChecks();
        string[] expected = { "deepseek", "gemini", "claude", "gpt", "grok", "glm", "kimi", "minimax", "qwen" };
        var rows = new List<object>();
        foreach (string id in expected)
        {
            var skin = skins.SingleOrDefault(s => s.Id == id) ?? throw new InvalidDataException("缺少皮肤：" + id);
            using var bmp = new System.Drawing.Bitmap(skin.ImagePath);
            int transparent = 0, opaque = 0, samples = 0;
            for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 80))
                for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 80))
                {
                    byte a = bmp.GetPixel(x, y).A;
                    if (a == 0) transparent++;
                    if (a > 240) opaque++;
                    samples++;
                }
            if (transparent < samples * .05 || opaque < samples * .05)
                throw new InvalidDataException("皮肤不是有效透明立绘：" + id);
            rows.Add(new { id, width = bmp.Width, height = bmp.Height, transparentSamples = transparent, opaqueSamples = opaque, samples });
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "asset-validation.json"),
            JsonSerializer.Serialize(new { passed = true, count = rows.Count, skins = rows, parserChecks, live2DImportChecks, sceneImportChecks, movementChecks, deepSeekMotionChecks, footGeometryChecks, sceneTriggerChecks, companionTaskChecks, deepSeekStateChecks }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> RunMonitorRecoveryCheck()
    {
        string output = Path.Combine(AppContext.BaseDirectory, "monitor-recovery-validation.json");
        try
        {
            object checks = await CodexMonitorRecoveryChecks.RunAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, version = AppVersion.Number, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, version = AppVersion.Number, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static async Task<int> RunSecurityCheck(bool installedPeer)
    {
        string output = Path.Combine(AppContext.BaseDirectory, installedPeer ? "pipe-trust-validation.json" : "security-validation.json");
        try
        {
            object endpoints = CodexEndpointPolicyChecks.Run();
            object pipe = await CodexPipeTrustChecks.RunAsync(installedPeer);
            object? storage = installedPeer ? null : CodexConnectionStoreChecks.Run(Path.GetTempPath());
            object? privacy = installedPeer ? null : await CodexPrivacyChecks.RunAsync();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, endpoints, pipe, storage, privacy }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex) }));
            return 1;
        }
    }

    private static async Task<int> RunConnectionCheck(bool pairing)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "connection-validation.json");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool pairingSlotCreated = true;
            using var pairingGuard = pairing ? new Mutex(false, @"Local\AIPet.DesktopPet", out pairingSlotCreated) : null;
            // The helper normally checks this as well; direct CLI pairing must not
            // race a running pet writing its previous connection record.
            if (pairing && !pairingSlotCreated) throw new InvalidOperationException("请先退出桌宠再配对。");
            using var connection = new CodexConnection(explicitPairing: pairing);
            var usage = CodexParser.ParseUsage(await connection.CallAsync("get_usage_limits", new { }, timeout.Token));
            var tasks = CodexParser.ParseTasks(await connection.CallAsync("list_threads", new { limit = 50 }, timeout.Token));
            connection.Disconnect();
            await connection.EnsureConnectedAsync(timeout.Token);
            var reconnected = CodexParser.ParseTasks(await connection.CallAsync("list_threads", new { limit = 5 }, timeout.Token));
            var report = new { passed = true, checkedAt = DateTimeOffset.Now, readOnly = true, pairing, independentContext = IndependentProcessLauncher.IsIndependentContext(), peerVerified = true, messageReads = 0, protectedRecord = true, buckets = usage.Buckets.Count, codexTasks = tasks.Count, activeTasks = tasks.Count(t => t.IsRunning), reconnectVerified = true, reconnectedTaskCount = reconnected.Count };
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            if (pairing) await connection.CommitPairingAsync();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = DiagnosticError(ex), hint = "请检查 Codex 官方安装来源、是否已退出桌宠、配对上下文及目录权限。" }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
    internal static void Log(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet");
            Directory.CreateDirectory(dir);
            string details = DiagnosticError(ex);
            if (GraphicsFailure.IsFatal(ex))
            {
                // Method names only: no exception message, local paths, account
                // identifiers, message text or connection material in the log.
                var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
                string methods = string.Join(" > ", (frames ?? Array.Empty<System.Diagnostics.StackFrame>()).Take(8)
                    .Select(frame => frame.GetMethod()).Where(method => method != null)
                    .Select(method => method!.DeclaringType?.FullName + "." + method.Name));
                details += " app=" + AppVersion.Number + " runtime=" + Environment.Version + " methods=" + methods;
            }
            File.AppendAllText(Path.Combine(dir, "error.log"), DateTimeOffset.Now + " " + details + "\n");
        }
        catch { }
        // UI subscribers and fire-and-forget renderer tasks sometimes log an
        // error before it reaches DispatcherUnhandledException. Fatal WPF
        // failures still need the same one-shot process recovery in that case.
        GraphicsFailure.Report(ex);
    }
    internal static string DiagnosticError(Exception ex) => ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + ")";
}

internal sealed class PetApplication : Application
{
    private readonly IReadOnlyList<SkinInfo> _skins;
    private readonly bool _smoke;
    private readonly bool _integration;
    private readonly bool _live2DTest;
    private CodexMonitor? _monitor;
    private readonly CompanionTaskRouter _companionTasks = new();
    private CodexDashboard? _dashboard;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitRegistration;
    private PetWindow? _pet;
    private SettingsWindow? _settingsWindow;
    private OllamaChatWindow? _chatWindow;
    private PetBehaviorCoordinator? _behavior;
    private PetCompanionUi? _companionUi;
    private EventWaitHandle? _chatSignal;
    private RegisteredWaitHandle? _chatRegistration;
    private Live2DLibraryWindow? _live2DWindow;
    private SceneAnimationLibraryWindow? _sceneWindow;
    private TrayService? _tray;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _signalRegistration;
    private DispatcherTimer? _smokeTimer;
    private int _smokeStep;
    private readonly List<object> _smokeSteps = new();

    private readonly string? _live2DTestModel;
    private readonly string? _sceneTestManifest;
    private readonly bool _movementTest;
    private readonly bool _deepSeekStateTest;
    private readonly bool _fallLandTest;
    private readonly bool _dragStateTest;
    private bool _errorDialogOpen;
    private DateTimeOffset _lastErrorDialog;

    public PetApplication(IReadOnlyList<SkinInfo> skins, bool smoke, bool integration, bool live2DTest = false, string? live2DTestModel = null, string? sceneTestManifest = null, bool movementTest = false, bool deepSeekStateTest = false, bool fallLandTest = false, bool dragStateTest = false)
    {
        _skins = skins;
        _smoke = smoke;
        _integration = integration;
        _live2DTest = live2DTest;
        _live2DTestModel = live2DTestModel;
        _sceneTestManifest = sceneTestManifest;
        _movementTest = movementTest;
        _deepSeekStateTest = deepSeekStateTest;
        _fallLandTest = fallLandTest;
        _dragStateTest = dragStateTest;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!_smoke) GraphicsFailure.FatalHandler = _ => GraphicsRecovery.ExitAndRecover(_pet?.Settings);
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            LifecycleLog.RecordException(LifecycleEvent.DispatcherUnhandledException, e.Exception);
            Program.Log(e.Exception);
            if (_smoke)
            {
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), JsonSerializer.Serialize(new { passed = false, error = Program.DiagnosticError(e.Exception) }));
                Shutdown(1);
            }
            else if (!_errorDialogOpen && DateTimeOffset.UtcNow - _lastErrorDialog > TimeSpan.FromSeconds(30))
            {
                _errorDialogOpen = true;
                _lastErrorDialog = DateTimeOffset.UtcNow;
                try { MessageBox.Show("桌宠遇到问题：" + e.Exception.Message, AppVersion.ProductLabel, MessageBoxButton.OK, MessageBoxImage.Warning); }
                finally { _errorDialogOpen = false; }
            }
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources = Ui.CreateResources();
        var settings = _smoke ? new DesktopSettings() : DesktopSettings.Load();
        if (!_smoke) AnimationPresetSelection.Apply(settings, Environment.GetCommandLineArgs());
        _pet = new PetWindow(_skins, settings, persist: !_smoke);
        MainWindow = _pet;
        _pet.OpenSettingsRequested += ShowSettings;
        _pet.OpenLive2DRequested += ShowLive2D;
        _pet.OpenScenesRequested += ShowScenes;
        _pet.OpenDashboardRequested += ShowDashboard;
        _pet.OpenChatRequested += ShowChat;
        _pet.SettingsChanged += () => { _chatWindow?.RefreshCharacter(); _chatWindow?.RefreshBehaviorConfiguration(); _behavior?.RefreshMode(); };
        _companionUi = new PetCompanionUi(_pet, ShowChat, ShowSettings, ShowDashboard);
        _pet.OpenQuickActionsRequested += _companionUi.ToggleActions;
        _pet.ExitRequested += () => RequestExit(LifecycleEvent.ExitPetMenu);
        _tray = new TrayService();
        _tray.MenuOpenChanged += open => Dispatcher.Invoke(() => _pet.SetTrayMenuOpen(open));
        _tray.ShowRequested += () => Dispatcher.Invoke(() => { _pet.Show(); _pet.ClampPosition(); });
        _tray.HideRequested += () => Dispatcher.Invoke(() => _pet.Hide());
        _tray.SettingsRequested += () => Dispatcher.Invoke(ShowSettings);
        _tray.DashboardRequested += () => Dispatcher.Invoke(ShowDashboard);
        _tray.ChatRequested += () => Dispatcher.Invoke(ShowChat);
        _tray.ExitRequested += () => Dispatcher.Invoke(() => RequestExit(LifecycleEvent.ExitTrayMenu));
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIPet.ShowSettings");
        _signalRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowDashboard), null, -1, false);
        _chatSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIPet.ShowChat");
        _chatRegistration = ThreadPool.RegisterWaitForSingleObject(_chatSignal, (_, _) => Dispatcher.BeginInvoke(ShowChat), null, -1, false);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIPet.Exit");
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) => Dispatcher.BeginInvoke(() => RequestExit(LifecycleEvent.ExitCommand)), null, -1, false);
        if (!_smoke || _integration)
        {
            _monitor = new CodexMonitor(settings);
            _monitor.Changed += () =>
            {
                _companionTasks.Observe(_monitor.Snapshot);
                _pet.SetCodexStatus(_companionTasks.MainPetSnapshot);
                // Dot state is maintained independently; its visual companion
                // remains disabled until a later release supplies that surface.
            };
            _monitor.VerifiedTransition += _pet.OnVerifiedSceneNotice;
            _monitor.Notice += notice => { _pet.Say(notice.Title + "：" + notice.Message); _tray.ShowBalloon(notice.Title, notice.Message); if (notice.Kind == "complete") _pet.Bounce(); };
            if (!_smoke) _monitor.Start();
        }
        _pet.Show();
        if (!_smoke) _behavior = new PetBehaviorCoordinator(_pet, () => _chatWindow, () => EnsureChatWindow(showErrors: false));
        if (_smoke)
        {
            ShowSettings();
            _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _smokeTimer.Tick += SmokeTick;
            _smokeTimer.Start();
        }
        else
        {
            _pet.Say(GraphicsRecovery.CompatibilityMode ? "已进入显示兼容模式，重启桌宠可重试动画。" : "点我会回应，右键图标栏可以聊天。别一直戳哦～");
            if (GraphicsRecovery.IsRecoveryLaunch)
                _tray.ShowBalloon("桌宠已恢复", GraphicsRecovery.CompatibilityMode ? "显示设备连续异常，已暂时使用静态待机；皮肤与动画设置已保留。" : "显示渲染异常后已自动重启，原设置已保留。");
            else if (Environment.GetCommandLineArgs().Contains("--chat")) ShowChat();
            else if (Environment.GetCommandLineArgs().Contains("--scenes")) ShowScenes();
            else if (Environment.GetCommandLineArgs().Contains("--live2d")) ShowLive2D(); else ShowDashboard();
        }
    }

    private async void SmokeTick(object? sender, EventArgs e)
    {
        try
        {
            if (_pet == null) return;
            if (_smokeStep < _skins.Count)
            {
                _pet.SelectSkin(_skins[_smokeStep].Id);
                _pet.SetSize(200 + (_smokeStep % 3) * 40);
                _pet.SetMotion(_smokeStep % 2 == 0);
                _smokeSteps.Add(new { skin = _pet.CurrentSkin.Id, size = _pet.Settings.Size, transparentWindow = _pet.AllowsTransparency, topmost = _pet.Topmost, visible = _pet.IsVisible });
                _smokeStep++;
                return;
            }
            _smokeTimer?.Stop();
            object? codexChecks = null;
            if (_integration && _monitor != null)
            {
                await _monitor.RefreshAsync(true);
                if (!_monitor.Snapshot.Connected) throw new InvalidOperationException(_monitor.Snapshot.Error ?? "Codex未连接");
                // Verify both the initial refresh and a later dispatcher-timer
                // tick while the actual WPF subscribers are attached.
                int completedRefreshes = 0;
                DateTimeOffset? observedRefresh = _monitor.Snapshot.LastUpdated;
                void ObserveRefresh()
                {
                    var state = _monitor.Snapshot;
                    if (state.Connected && !state.Refreshing && state.LastUpdated != observedRefresh)
                    { observedRefresh = state.LastUpdated; completedRefreshes++; }
                }
                _monitor.Changed += ObserveRefresh;
                try
                {
                    _monitor.Start();
                    var until = DateTimeOffset.UtcNow.AddSeconds(25);
                    while (completedRefreshes < 2 && DateTimeOffset.UtcNow < until) await Task.Delay(200);
                    if (completedRefreshes < 2) throw new InvalidOperationException("Codex 自动刷新未恢复。");
                }
                finally { _monitor.Changed -= ObserveRefresh; }
                ShowDashboard();
                var selected = _monitor.Snapshot.Tasks.FirstOrDefault(t => t.Id == _monitor.CallerThreadId) ?? _monitor.Snapshot.Tasks.FirstOrDefault();
                if (_monitor.MessagesEnabled && selected != null) await _monitor.SelectTaskAsync(selected);
                if (_monitor.ConversationError != null) throw new InvalidOperationException(_monitor.ConversationError);
                if (_monitor.MessagesEnabled && selected?.Id == _monitor.CallerThreadId && _monitor.Conversation?.Messages.Count == 0) throw new InvalidDataException("当前真实任务的公开消息未载入。");
                codexChecks = new { connected = true, tasks = _monitor.Snapshot.Tasks.Count, buckets = _monitor.Snapshot.Buckets.Count, publicMessages = _monitor.Conversation?.Messages.Count ?? 0, automaticPollingVerified = true, completedRefreshes };
            }
            object? live2DChecks = _live2DTest ? await Live2DIntegrationChecks.RunAsync(_pet, _live2DTestModel) : null;
            object? movementRegressionChecks = null, footVisualChecks = null;
            if (_movementTest)
            {
                _pet.SelectSkin("deepseek");
                footVisualChecks = await FootVisualChecks.RunAsync(_pet);
                movementRegressionChecks = await MovementRegressionChecks.RunAsync(_pet);
            }
            object? sceneChecks = null, movementIntegrationChecks = null;
            if (_sceneTestManifest != null)
            {
                _pet.SelectSkin("deepseek");
                _pet.SetMotion(true);
                if (_live2DTestModel != null)
                {
                    var model = new Live2DModelCatalog().Validate(_live2DTestModel, "integration-deepseek");
                    if (!await _pet.SelectLive2DAsync(model)) throw new InvalidOperationException("场景测试前无法载入真实 Live2D。");
                }
                movementIntegrationChecks = await DesktopMovementIntegrationChecks.RunAsync(_pet);
                sceneChecks = await SceneAnimationIntegrationChecks.RunAsync(_pet, _sceneTestManifest);
            }
            object? deepSeekStateChecks = _deepSeekStateTest ? await DeepSeekAnimationIntegrationChecks.RunAsync(_pet) : null;
            object? fallLandChecks = _fallLandTest ? await FallLandingIntegrationChecks.RunAsync(_pet) : null;
            object? dragStateChecks = _dragStateTest ? await DragAnimationIntegrationChecks.RunAsync(_pet) : null;
            var nativeWindowChecks = SmokeChecks.VerifyWindow(_pet);
            _pet.SetClickThrough(true);
            _pet.SetClickThrough(false);
            _pet.Hide();
            _pet.Show();
            _pet.ResetPosition();
            _settingsWindow?.SavePreview(Path.Combine(AppContext.BaseDirectory, "settings-preview.png"));
            _pet.SavePreview(Path.Combine(AppContext.BaseDirectory, "pet-preview.png"));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), JsonSerializer.Serialize(
                new { passed = true, skinsExercised = _smokeSteps.Count, steps = _smokeSteps, clickThroughToggled = true, visibilityToggled = true, settingsPersisted = false, nativeWindowChecks, codexChecks, live2DChecks, sceneChecks, movementIntegrationChecks, movementRegressionChecks, footVisualChecks, deepSeekStateChecks, fallLandChecks, dragStateChecks },
                new JsonSerializerOptions { WriteIndented = true }));
            _smokeTimer?.Stop();
            Shutdown();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"), JsonSerializer.Serialize(new { passed = false, error = Program.DiagnosticError(ex) }));
            _smokeTimer?.Stop();
            Shutdown(1);
        }
    }

    private void ShowLive2D()
    {
        if (_pet == null) return;
        if (_live2DWindow == null)
        {
            _live2DWindow = new Live2DLibraryWindow(_pet);
            _live2DWindow.Closed += (_, _) => _live2DWindow = null;
        }
        _live2DWindow.Show();
        if (_live2DWindow.WindowState == WindowState.Minimized) _live2DWindow.WindowState = WindowState.Normal;
        _live2DWindow.Activate();
    }

    private void ShowScenes()
    {
        if (_pet == null) return;
        if (_sceneWindow == null)
        {
            _sceneWindow = new SceneAnimationLibraryWindow(_pet);
            _sceneWindow.Closed += (_, _) => _sceneWindow = null;
        }
        _sceneWindow.Show();
        if (_sceneWindow.WindowState == WindowState.Minimized) _sceneWindow.WindowState = WindowState.Normal;
        _sceneWindow.Activate();
    }

    private void ShowSettings()
    {
        if (_pet == null) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_pet, _skins, ShowDashboard, ShowChat, SetLlmBehaviorMode);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ShowChat()
    {
        if (_pet == null) return;
        if (!_pet.IsVisible) { _pet.Show(); _pet.ClampPosition(); }
        var chat = EnsureChatWindow(showErrors: true);
        if (chat == null) return;
        chat.RefreshCharacter();
        _companionUi?.ShowChat(chat);
        _behavior?.RefreshMode();
    }

    private void SetLlmBehaviorMode(bool enabled)
    {
        if (_pet == null) return;
        bool previous = _pet.Settings.LlmBehaviorEnabled;
        try { _pet.Settings.LlmBehaviorEnabled = enabled; _pet.SaveNow(); }
        catch { _pet.Settings.LlmBehaviorEnabled = previous; throw; }
        _chatWindow?.RefreshBehaviorConfiguration();
        _behavior?.RefreshMode();
    }

    private OllamaChatWindow? EnsureChatWindow(bool showErrors)
    {
        if (_pet == null) return null;
        if (_chatWindow == null)
        {
            ChatMemoryStore? store = null;
            try
            {
                store = new ChatMemoryStore(ChatMemoryStore.DefaultPath);
                _chatWindow = new OllamaChatWindow(_pet.Settings, () => _pet.CurrentDisplayName,
                    _pet.SetChatBusy, _pet.Say, _pet.SaveNow, chatStore: store, behaviorHost: _pet,
                    answerCompleted: _pet.SayChatAnswer, stopSpeech: _pet.StopSpeech,
                    voiceInputActive: _pet.SetVoiceInputActive,
                    voiceChatSessionActive: _pet.SetVoiceChatSessionActive,
                    streamingSpeech: _pet.CreateChatSpeechCallbacks());
            }
            catch (Exception ex)
            {
                store?.Dispose();
                LifecycleLog.RecordException(LifecycleEvent.ChatStorageFailure, ex);
                if (showErrors) MessageBox.Show("本地聊天记录暂时无法打开，原记录文件已保留。请检查磁盘空间和文件权限后重试。\n\n" + ChatMemoryStore.DefaultPath,
                    AppVersion.ProductLabel, MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            _chatWindow.Closed += (_, _) => { _chatWindow = null; _behavior?.RefreshMode(); };
        }
        return _chatWindow;
    }

    private void ShowDashboard()
    {
        if (_monitor == null || _pet == null) { ShowSettings(); return; }
        if (_dashboard == null)
        {
            _dashboard = new CodexDashboard(_monitor, _pet.Settings, ShowSettings, speechHost: _pet);
            _dashboard.Closed += (_, _) => _dashboard = null;
        }
        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }
    private void RequestExit(LifecycleEvent reason)
    {
        LifecycleLog.Record(reason);
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        LifecycleLog.Record(e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? LifecycleEvent.SessionLogoff : LifecycleEvent.SessionShutdown);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LifecycleLog.Record(LifecycleEvent.ApplicationExit, e.ApplicationExitCode);
        GraphicsFailure.FatalHandler = null;
        _smokeTimer?.Stop();
        _behavior?.Dispose();
        _companionUi?.Dispose();
        _chatWindow?.Shutdown();
        _monitor?.Dispose();
        _exitRegistration?.Unregister(null);
        _exitSignal?.Dispose();
        _signalRegistration?.Unregister(null);
        _showSignal?.Dispose();
        _chatRegistration?.Unregister(null);
        _chatSignal?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}

internal static class AssetLoader
{
    public static BitmapImage Load(string file, int width = 700)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.UriSource = new Uri(file, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
