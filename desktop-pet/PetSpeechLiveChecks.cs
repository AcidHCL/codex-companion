using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace DesktopPet;

// Explicit opt-in local fixture: speaks fixed text, with no Codex/Ollama or stored chat.
internal static class PetSpeechLiveChecks
{
    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int result = 1;
        app.Startup += async (_, _) =>
        {
            var clock = Stopwatch.StartNew(); double? firstAudioSeconds = null; string? error = null;
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var voice = new PetSpeechService(new DesktopSettings { SkinId = "deepseek", SpeechEnabled = true, SpeechVolume = .6 }, app.Dispatcher);
            voice.Changed += () =>
            {
                if (voice.IsSpeaking) firstAudioSeconds ??= clock.Elapsed.TotalSeconds;
                else if (firstAudioSeconds != null && voice.StatusText.Contains("待命")) complete.TrySetResult();
                else if (!voice.StatusText.StartsWith("正在合成参考音色", StringComparison.Ordinal) &&
                    !voice.StatusText.StartsWith("正在准备连续语音", StringComparison.Ordinal) &&
                    !voice.StatusText.StartsWith("正在缓冲连续语音", StringComparison.Ordinal) &&
                    !voice.StatusText.Contains("待命")) complete.TrySetException(new InvalidOperationException(voice.StatusText));
            };
            try
            {
                voice.Speak("哼，任务完成啦，记得请本鲸吃白米饭！", PetSpeechSource.Preview);
                await complete.Task.WaitAsync(TimeSpan.FromMinutes(9));
                result = 0;
            }
            catch (Exception ex) { error = ex.Message; }
            voice.Dispose();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-live-validation.json"), JsonSerializer.Serialize(new
            { passed = result == 0, firstAudioSeconds, elapsedSeconds = clock.Elapsed.TotalSeconds, error, realAudioOutput = true, text = "fixed_test_phrase", realAccountRequests = 0, userSettingsWrites = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown(result);
        };
        app.Run(); return result;
    }
}
