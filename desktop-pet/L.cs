using System;

namespace DesktopPet;

// UI and built-in prompts use the same process-wide language. Changing the
// saved preference takes effect at the next startup, so active conversations,
// voice jobs and unsaved editor drafts are never torn down to change labels.
internal static class L
{
    internal const string Chinese = "zh-CN";
    internal const string English = "en-US";
    internal static string Language { get; private set; } = Chinese;
    internal static bool IsEnglish => Language == English;
    internal static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? English : Chinese;
    internal static void Initialize(string? language) => Language = Normalize(language);
    internal static string T(string chinese, string english) => IsEnglish ? english : chinese;
}
