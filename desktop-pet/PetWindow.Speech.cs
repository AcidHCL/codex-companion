using System;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private PetSpeechService? _voice;
    private bool _voiceInputActive;
    public string SpeechStatusText => !string.Equals(CurrentSkin.Id, "deepseek", StringComparison.OrdinalIgnoreCase)
        ? "语音目前仅适配 DeepSeek" : _voice?.StatusText ?? "语音尚未初始化";
    public bool IsSpeaking => _voice?.IsSpeaking == true;

    private void InitializeSpeech()
    {
        // Isolated visual checks use persist:false and must never make sound.
        _voice = new PetSpeechService(Settings, Dispatcher, enabledForProcess: _persist);
        _voice.Changed += SpeechServiceChanged;
        RefreshSpeechSuspension();
    }

    private void SpeechServiceChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(SpeechServiceChanged)); return; }
        // A playback/status update is not a preference change and must not save settings.
        SettingsChanged?.Invoke();
    }

    private void RefreshSpeechSuspension()
    {
        bool unavailable = _closed || !IsVisible || _displayRenderingPaused ||
            DisplayLifecycle?.AllowsCompanionInteraction == false ||
            !string.Equals(CurrentSkin.Id, "deepseek", StringComparison.OrdinalIgnoreCase);
        bool songAudible = IsManualSoundScene && Settings.SoundEnabled && Settings.SoundVolume > 0;
        _voice?.SetSuspended(unavailable || songAudible || _voiceInputActive);
    }

    private void SpeakText(string text, PetSpeechSource source)
    {
        RefreshSpeechSuspension();
        if (string.Equals(CurrentSkin.Id, "deepseek", StringComparison.OrdinalIgnoreCase))
        {
            // Audio is optional; a device/backend error must never undo a saved reply.
            try { _voice?.Speak(text, source); }
            catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
        }
    }

    public void SayChatAnswer(string fullAnswer, string bubbleText)
    {
        ShowSpeechBubble(bubbleText);
        SpeakText(fullAnswer, PetSpeechSource.Chat);
    }

    public void SetSpeechEnabled(bool enabled)
    {
        Settings.SpeechEnabled = enabled;
        _voice?.ApplySettings();
        RefreshSpeechSuspension();
        SaveNow();
        SettingsChanged?.Invoke();
    }

    public void SetSpeechVolume(double volume)
    {
        Settings.SpeechVolume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : .6;
        _voice?.ApplySettings();
        SaveNow();
        SettingsChanged?.Invoke();
    }

    public void PreviewSpeech() => SpeakText("哼，本鲸已经准备好啦！任务完成后记得请我吃一碗白米饭。", PetSpeechSource.Preview);
    public void StopSpeech() => _voice?.Stop();

    public Task PreloadSpeechModelAsync() => _voice?.PreloadAsync() ?? Task.CompletedTask;
    public Task UnloadSpeechModelAsync() => _voice?.UnloadAsync() ?? Task.CompletedTask;
    public void SetVoiceChatSessionActive(bool active) => _voice?.SetChatSessionActive(active);

    public void SetVoiceInputActive(bool active)
    {
        if (_voiceInputActive == active) return;
        _voiceInputActive = active;
        if (active && IsManualSoundScene) StopScenePreview();
        RefreshSpeechSuspension();
    }

    private void DisposeSpeech()
    {
        if (_voice == null) return;
        _voice.Changed -= SpeechServiceChanged;
        _voice.Dispose();
        _voice = null;
    }
}
