using System;
using System.Collections.Generic;

namespace DesktopPet.WorkCompanion;

// Exact process-name hints, not claims about screen contents or user intent.
// Browsers and launchers remain general daily activity unless the user supplies
// a classification. Never inspect titles, executable paths, children or memory.
internal static class CompanionActivityClassifier
{
    private static readonly HashSet<string> WorkApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders", "devenv", "pycharm64", "idea64", "rider64", "clion64", "webstorm64",
        "studio64", "notepad++", "sublime_text", "winword", "excel", "powerpnt", "onenote", "acrobat",
        "acrord32", "zotero", "obsidian", "notion", "typora", "texstudio", "texworks", "sumatrapdf",
        "matlab", "rstudio", "blender", "photoshop", "illustrator", "figma", "inkscape", "krita"
    };
    private static readonly HashSet<string> GameApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "dota2", "league of legends", "valorant-win64-shipping", "genshinimpact", "yuanshen",
        "starrail", "zenlesszonezero", "overwatch", "fortniteclient-win64-shipping", "minecraft.windows",
        "stardew valley", "terraria", "eldenring", "bg3", "bg3_dx11", "cyberpunk2077", "monsterhunterworld",
        "monsterhunterwilds", "warframe.x64", "r5apex", "hl2", "b1-win64-shipping", "witcher3", "factorio",
        "satisfactory", "palworld-win64-shipping", "hollow_knight", "hades", "hades2", "civilizationvi",
        "civilizationvi_dx12", "stellaris", "rimworldwin64", "sims4", "ts4_x64", "wow", "wowclassic",
        "ffxiv_dx11", "rocketleague", "robloxplayerbeta", "osu!", "osu", "balatro", "slaythespire"
    };
    private static readonly HashSet<string> MediaApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayermini", "potplayermini64",
        "spotify", "cloudmusic", "qqmusic", "foobar2000", "musicbee", "aimp", "itunes", "applemusic",
        "wmplayer", "microsoft.media.player", "groove", "netflix", "bilibili"
    };

    internal static WorkActivity Classify(string? app, IReadOnlyDictionary<string, string>? overrides = null)
    {
        string name = Normalize(app);
        if (name.Length == 0) return WorkActivity.Daily;
        if (overrides != null)
        {
            int inspected = 0;
            foreach (var pair in overrides)
            {
                if (++inspected > 64) break;
                if (string.Equals(name, Normalize(pair.Key), StringComparison.OrdinalIgnoreCase) &&
                    TryParse(pair.Value, out var selected)) return selected;
            }
        }
        if (WorkApps.Contains(name)) return WorkActivity.Work;
        if (GameApps.Contains(name)) return WorkActivity.Game;
        if (MediaApps.Contains(name)) return WorkActivity.Media;
        return WorkActivity.Daily;
    }

    internal static bool HasBuiltInCategory(string? app)
    {
        string name = Normalize(app);
        return WorkApps.Contains(name) || GameApps.Contains(name) || MediaApps.Contains(name);
    }

    internal static string Key(WorkActivity activity) => activity switch
    {
        WorkActivity.Work => "work", WorkActivity.Game => "game", WorkActivity.Media => "media", _ => "daily"
    };

    internal static string Label(WorkActivity activity) => activity switch
    {
        WorkActivity.Work => L.T("工作与学习", "Work & study"),
        WorkActivity.Game => L.T("游戏", "Gaming"),
        WorkActivity.Media => L.T("影音娱乐", "Music & video"),
        _ => L.T("日常使用", "Everyday use")
    };

    internal static bool TryParse(string? value, out WorkActivity activity)
    {
        activity = (value ?? "").Trim().ToLowerInvariant() switch
        {
            "work" => WorkActivity.Work, "game" => WorkActivity.Game,
            "media" => WorkActivity.Media, "daily" => WorkActivity.Daily, _ => (WorkActivity)(-1)
        };
        return Enum.IsDefined(activity);
    }

    private static string Normalize(string? app)
    {
        string name = (app ?? "").Trim();
        if (name.Length is < 1 or > 80 || name.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0) return "";
        foreach (char character in name) if (char.IsControl(character)) return "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        return name;
    }
}
