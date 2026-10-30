using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed partial class PetWindow
{
    private PetSpeechService? _voice;
    private bool _voiceInputActive;
    private bool _voiceStudioActive;
    internal bool VoiceStudioActive => _voiceStudioActive;
    public string SpeechStatusText => !PetVoiceProfiles.IsEnabled(Settings.GetVoiceProfile(CurrentSkin.Id))
        ? "当前角色尚未选择语音模型" : _voice?.StatusText ?? "语音尚未初始化";
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
            !PetVoiceProfiles.IsEnabled(Settings.GetVoiceProfile(CurrentSkin.Id));
        bool songAudible = IsManualSoundScene && Settings.SoundEnabled && Settings.SoundVolume > 0;
        _voice?.SetSuspended(unavailable || songAudible || _voiceInputActive || _voiceStudioActive);
    }

    private void SpeakText(string text, PetSpeechSource source)
    {
        RefreshSpeechSuspension();
        if (PetVoiceProfiles.IsEnabled(Settings.GetVoiceProfile(CurrentSkin.Id)))
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

    public void SetCharacterVoiceProfile(string skinId, PetVoiceProfile profile)
    {
        var previous = Settings.GetVoiceProfile(skinId);
        bool existed = Settings.CharacterVoices.ContainsKey(skinId);
        Settings.SetVoiceProfile(skinId, profile);
        try { if (_persist) Settings.Save(); }
        catch
        {
            if (existed) Settings.SetVoiceProfile(skinId, previous);
            else Settings.CharacterVoices.Remove(skinId);
            throw;
        }
        if (string.Equals(CurrentSkin.Id, skinId, StringComparison.OrdinalIgnoreCase))
        {
            _voice?.ApplySettings();
            RefreshSpeechSuspension();
        }
        SettingsChanged?.Invoke();
    }

    internal void DeleteSavedVoiceReference(string referenceId, Action deleteReference)
    {
        Dispatcher.VerifyAccess();
        if (!PetVoiceProfiles.IsReferenceId(referenceId))
            throw new ArgumentException("只能删除自行保存的音色，内置默认音色不可删除。", nameof(referenceId));
        ArgumentNullException.ThrowIfNull(deleteReference);
        var previous = new Dictionary<string, PetVoiceProfile>(Settings.CharacterVoices, StringComparer.OrdinalIgnoreCase);
        var affected = PetVoiceProfiles.Characters.Where(character =>
        {
            var profile = Settings.GetVoiceProfile(character.Id);
            return profile.ModelId == "saved-voice" && profile.ReferenceId == referenceId;
        }).Select(character => character.Id).ToArray();
        // Persist every referencing character in one atomic settings write before
        // retiring the reference. A failed save must leave the voice library intact.
        foreach (string skinId in affected) Settings.SetVoiceProfile(skinId, PetVoiceProfiles.DefaultForSkin(skinId));
        try { if (_persist && affected.Length > 0) Settings.Save(); }
        catch { Settings.CharacterVoices = previous; throw; }
        var fallback = new Dictionary<string, PetVoiceProfile>(Settings.CharacterVoices, StringComparer.OrdinalIgnoreCase);
        try { deleteReference(); }
        catch (Exception deleteError)
        {
            Settings.CharacterVoices = previous;
            try { if (_persist && affected.Length > 0) Settings.Save(); }
            catch (Exception restoreError)
            {
                // The persisted fallback is still valid. Keep memory and disk in
                // agreement instead of reintroducing a potentially broken selection.
                Settings.CharacterVoices = fallback;
                RefreshVoiceSelectionAfterDelete(affected);
                throw new InvalidOperationException("音色未删除，恢复原声音设置失败；受影响角色已保持默认音色。", new AggregateException(deleteError, restoreError));
            }
            RefreshVoiceSelectionAfterDelete(affected);
            throw;
        }
        RefreshVoiceSelectionAfterDelete(affected);
    }

    private void RefreshVoiceSelectionAfterDelete(string[] affected)
    {
        if (affected.Contains(CurrentSkin.Id, StringComparer.OrdinalIgnoreCase))
        {
            _voice?.ApplySettings();
            RefreshSpeechSuspension();
        }
        SettingsChanged?.Invoke();
    }

    public void PreviewSpeech() => SpeakText(CurrentSkin.Id.ToLowerInvariant() switch
    {
        "deepseek" => "哼，本鲸已经准备好啦！任务完成后记得请我吃一碗白米饭。",
        "gpt" => "宝库先锁好了。有什么事，交给小龙娘看看。放心，认真帮忙可不收金币。",
        _ => "你好，我是" + CurrentSkin.Name + "。声音已经准备好了，我们开始聊天吧。"
    }, PetSpeechSource.Preview);
    public void StopSpeech() => _voice?.Stop();

    public Task PreloadSpeechModelAsync() => _voiceStudioActive
        ? Task.FromException(new InvalidOperationException("音色工坊正在工作，请先结束生成或试听。")) : _voice?.PreloadAsync() ?? Task.CompletedTask;
    public Task UnloadSpeechModelAsync() => _voiceStudioActive
        ? Task.FromException(new InvalidOperationException("音色工坊正在工作，请使用工坊的取消或停止按钮。")) : _voice?.UnloadAsync() ?? Task.CompletedTask;
    internal async Task BeginVoiceStudioAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (!_persist || _closed) throw new InvalidOperationException("请从桌宠打开设置使用音色工坊。");
        if (_voiceStudioActive) throw new InvalidOperationException("音色工坊已有操作正在进行。");
        token.ThrowIfCancellationRequested();
        _voiceStudioActive = true;
        _voice?.SetStudioBusy(true);
        RefreshSpeechSuspension();
        SettingsChanged?.Invoke();
        try
        {
            if (_voice != null) await _voice.UnloadAsync();
            token.ThrowIfCancellationRequested();
        }
        catch { EndVoiceStudio(); throw; }
    }
    internal void EndVoiceStudio()
    {
        Dispatcher.VerifyAccess();
        _voiceStudioActive = false;
        _voice?.SetStudioBusy(false);
        RefreshSpeechSuspension();
        if (!_closed) SettingsChanged?.Invoke();
    }
    internal Task<PetVoiceHealth?> ReadSpeechModelHealthAsync(CancellationToken cancellationToken) =>
        _voice?.ReadModelHealthAsync(cancellationToken) ?? Task.FromResult<PetVoiceHealth?>(null);
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
