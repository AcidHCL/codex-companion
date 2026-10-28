using System;

namespace DesktopPet;

internal sealed record PetChatSpeechCallbacks(
    Func<long> Begin, Action<long, string> Append, Action<long> Complete,
    Action Cancel, Action<string> Bubble);

internal sealed partial class OllamaChatWindow
{
    private readonly PetChatSpeechCallbacks? _streamingSpeech;
    private PetSpeechIncrementalText? _spokenText;
    private long _spokenResponse;
    private bool _speechTurnStarted;

    private void BeginStreamingSpeech()
    {
        if (_streamingSpeech == null) return;
        _speechTurnStarted = true;
        _spokenText = new();
        try { _spokenResponse = _streamingSpeech.Begin(); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { _spokenResponse = 0; Program.Log(ex); }
    }

    private void AppendStreamingSpeech(string? content)
    {
        if (!_speechTurnStarted || _spokenText == null || _streamingSpeech == null || _spokenResponse <= 0) return;
        try
        {
            foreach (string text in _spokenText.Append(content)) _streamingSpeech.Append(_spokenResponse, text);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { CancelStreamingSpeechCore(); Program.Log(ex); }
    }

    private bool FinishStreamingSpeech(string bubbleText)
    {
        if (!_speechTurnStarted || _streamingSpeech == null) return false;
        try
        {
            if (_spokenResponse > 0 && _spokenText != null)
            {
                foreach (string text in _spokenText.Append(null, final: true)) _streamingSpeech.Append(_spokenResponse, text);
                _streamingSpeech.Complete(_spokenResponse);
            }
            _streamingSpeech.Bubble(bubbleText);
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { CancelStreamingSpeechCore(); Program.Log(ex); }
        finally { _spokenText = null; _spokenResponse = 0; _speechTurnStarted = false; }
        return true;
    }

    private void CancelStreamingSpeechCore()
    {
        try { _streamingSpeech?.Cancel(); }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); }
        _spokenText = null; _spokenResponse = 0;
    }

    private void CancelStreamingSpeech()
    {
        if (_speechTurnStarted) CancelStreamingSpeechCore();
        _speechTurnStarted = false;
    }
}
