namespace DesktopPet;

internal sealed partial class PetWindow
{
    public void SetOllamaKeepAliveMinutes(int minutes)
    {
        Dispatcher.VerifyAccess();
        int previous = Settings.OllamaKeepAliveMinutes;
        int next = ModelRetentionPolicy.NormalizeOllama(minutes);
        if (previous == next) return;
        Settings.OllamaKeepAliveMinutes = next;
        try { if (_persist) Settings.Save(); }
        catch { Settings.OllamaKeepAliveMinutes = previous; throw; }
        // Existing and replacement Ollama clients read this value on each new
        // request. Changing the preference never sends a request or loads a model.
        SettingsChanged?.Invoke();
    }
}
