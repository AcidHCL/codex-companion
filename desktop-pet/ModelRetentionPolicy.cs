namespace DesktopPet;

// Durations are minutes. -1 disables idle unloading; speech also supports the
// legacy chat-session lease at 0. Existing settings files retain their behavior.
internal static class ModelRetentionPolicy
{
    internal const int Never = -1;
    internal const int SpeechChatSession = 0;
    internal const int DefaultOllama = 5;
    internal static int NormalizeOllama(int value) => value is -1 or 1 or 5 or 10 or 30 or 60 ? value : DefaultOllama;
    internal static int NormalizeSpeech(int value) => value is -1 or 0 or 1 or 5 or 10 or 30 or 60 ? value : SpeechChatSession;
}
