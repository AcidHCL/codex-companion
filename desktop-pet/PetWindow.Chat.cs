namespace DesktopPet;

internal sealed partial class PetWindow
{
    // Local generation is activity of this companion, never a Codex/Dot task.
    public bool IsChatBusy { get; private set; }
    internal bool IsCompanionOpen { get; private set; }
    internal bool CompanionSurfacesAvailable => !_closed && IsVisible && (DisplayLifecycle?.AllowsCompanionInteraction ?? true);
    internal event System.Action? CompanionSurfacesUnavailable;

    internal void SetCompanionOpen(bool open)
    {
        if (IsCompanionOpen == open) return;
        IsCompanionOpen = open;
        if (!_closed) UpdateMovementAutonomy();
    }

    public void SetChatBusy(bool busy)
    {
        if (_closed || IsChatBusy == busy) return;
        IsChatBusy = busy;
        UpdateActivityPause();
        RefreshAutomaticScene();
        if (busy) ShowSpeechBubble(PetPersona.ForSkin(CurrentSkin.Id, CurrentDisplayName, Settings.OllamaModel).Thinking);
    }
}
