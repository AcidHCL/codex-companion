namespace DesktopPet;

internal sealed partial class PetWindow
{
    internal PetChatSpeechCallbacks CreateChatSpeechCallbacks() => new(
        () => { RefreshSpeechSuspension(); return _voice?.BeginChatResponse() ?? 0; },
        (id, text) => _voice?.AppendChatResponse(id, text),
        id => _voice?.CompleteChatResponse(id),
        StopSpeech,
        ShowSpeechBubble);
}
