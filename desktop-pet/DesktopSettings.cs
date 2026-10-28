using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopPet;

internal sealed class DesktopSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string SkinId { get; set; } = "deepseek";
    public string? Live2DModelId { get; set; }
    public string? ScenePackId { get; set; }
    public string DeepSeekAnimationPackId { get; set; } = BundledSceneAnimations.DeepSeekId;
    public bool SceneAutoEnabled { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = .35;
    public bool SpeechEnabled { get; set; } = true;
    public double SpeechVolume { get; set; } = .6;
    public bool MovementEnabled { get; set; }
    public double Size { get; set; } = 240;
    // Physical virtual-desktop pixels. Negative coordinates are valid on secondary monitors.
    public double? Left { get; set; }
    public double? Top { get; set; }
    public string? DpiMonitor { get; set; }
    public bool Topmost { get; set; } = true;
    public bool MotionEnabled { get; set; } = true;
    public bool ClickThrough { get; set; }
    public int RefreshSeconds { get; set; } = 5;
    public bool CodexEnabled { get; set; } = true;
    public bool CodexMessagesEnabled { get; set; } = false;
    public bool NotificationsEnabled { get; set; } = true;
    public bool ShowStatusBadge { get; set; } = true;
    public string OllamaAddress { get; set; } = OllamaEndpoint.DefaultAddress;
    public string OllamaModel { get; set; } = OllamaModels.FastModel;
    public bool WebSearchEnabled { get; set; } = true;
    public bool SharedMemoryEnabled { get; set; } = true;
    public bool LlmBehaviorEnabled { get; set; } = false;
    // Local ownership labels only. No message text, pairing data, or account data.
    public Dictionary<string, string> TaskSourceOverrides { get; set; } = new(StringComparer.Ordinal);
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet", "settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DesktopSettings();
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new DesktopSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine("Unable to read desktop pet settings: " + ex.Message);
            return new DesktopSettings();
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(SkinId)) SkinId = "deepseek";
        if (!BundledSceneAnimations.IsBuiltIn(DeepSeekAnimationPackId)) DeepSeekAnimationPackId = BundledSceneAnimations.DeepSeekId;
        if (BundledSceneAnimations.IsBuiltIn(ScenePackId)) DeepSeekAnimationPackId = ScenePackId!;
        RefreshSeconds = Math.Clamp(RefreshSeconds, 5, 120);
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 160, 440) : 240;
        SoundVolume = double.IsFinite(SoundVolume) ? Math.Clamp(SoundVolume, 0, 1) : .35;
        SpeechVolume = double.IsFinite(SpeechVolume) ? Math.Clamp(SpeechVolume, 0, 1) : .6;
        if (Left.HasValue && (!double.IsFinite(Left.Value) || Left.Value < int.MinValue || Left.Value > int.MaxValue)) Left = null;
        if (Top.HasValue && (!double.IsFinite(Top.Value) || Top.Value < int.MinValue || Top.Value > int.MaxValue)) Top = null;
        TaskSourceOverrides = TaskSourcePolicy.NormalizeOverrides(TaskSourceOverrides);
        OllamaAddress = OllamaEndpoint.Normalize(OllamaAddress);
        OllamaModel = OllamaModels.Normalize(OllamaModel);
    }

    public void Save()
    {
        Normalize();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
