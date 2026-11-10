using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPet.ScreenUnderstanding;

// Deliberately has no free-form description, title, screenshot, model output,
// executable path, or task content. Only an app's bounded classification lives here.
internal sealed record LearnedApplication(string Id, string App, string Identity, string Activity,
    double Confidence, DateTimeOffset LearnedAt, DateTimeOffset ExpiresAt, bool UserCorrected);

internal sealed class LearnedApplicationStore
{
    internal const int MaximumEntries = 256;
    internal const double MinimumConfidence = .85;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);
    private const int MaxBytes = 256 * 1024;
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _sync = new();
    private List<LearnedApplication> _items;

    internal LearnedApplicationStore(string? path = null, Func<DateTimeOffset>? now = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIPet", "learned-applications.json"));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _items = Sanitize(ScreenSettingsJson.Read<List<LearnedApplication>>(_path, MaxBytes) ?? new(), _now());
    }

    internal IReadOnlyList<LearnedApplication> List()
    { lock (_sync) return Current().OrderBy(item => item.App, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToArray(); }

    internal LearnedApplication? GetByApp(string app)
    {
        string? normalized = NormalizeApp(app);
        if (normalized == null) return null;
        lock (_sync)
        {
            var matches = Current().Where(item => item.App == normalized).Take(2).ToArray();
            // Multiple executable identities with the same basename must not be
            // collapsed, even if their saved categories happen to match.
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    internal LearnedApplication? GetByIdentity(string app, string identity)
    {
        string? normalized = NormalizeApp(app), normalizedIdentity = NormalizeIdentity(identity);
        if (normalized == null || normalizedIdentity == null) return null;
        lock (_sync) return Current().FirstOrDefault(item => item.App == normalized && item.Identity == normalizedIdentity);
    }

    internal bool SaveLearned(string app, string identity, string activity, double confidence)
    {
        string? normalized = NormalizeApp(app), normalizedIdentity = NormalizeIdentity(identity), category = NormalizeActivity(activity);
        if (normalized == null || normalizedIdentity == null || category == null ||
            !double.IsFinite(confidence) || confidence < MinimumConfidence || confidence > 1) return false;
        lock (_sync)
        {
            var items = Current().ToList();
            string id = IdFor(normalized, normalizedIdentity);
            var existing = items.FirstOrDefault(item => item.Id == id);
            if (existing?.UserCorrected == true) return false;
            if (existing != null) items.Remove(existing);
            if (items.Count >= MaximumEntries)
            {
                var oldest = items.Where(item => !item.UserCorrected).OrderBy(item => item.LearnedAt).FirstOrDefault();
                if (oldest == null) return false;
                items.Remove(oldest);
            }
            var now = _now();
            items.Add(new LearnedApplication(id, normalized, normalizedIdentity, category,
                Math.Round(confidence, 4), now, now + Lifetime, false));
            Save(items); return true;
        }
    }

    internal bool UpdateCategory(string id, string activity)
    {
        string? category = NormalizeActivity(activity);
        if (category == null) return false;
        lock (_sync)
        {
            var items = Current().ToList();
            int index = items.FindIndex(item => item.Id == id);
            if (index < 0) return false;
            var now = _now();
            items[index] = items[index] with
            { Activity = category, Confidence = 1, UserCorrected = true, LearnedAt = now, ExpiresAt = now + Lifetime };
            Save(items); return true;
        }
    }

    internal bool Delete(string id)
    {
        lock (_sync)
        {
            var items = Current().ToList();
            if (items.RemoveAll(item => item.Id == id) == 0) return false;
            Save(items); return true;
        }
    }

    internal void Clear() { lock (_sync) Save(new()); }

    private IEnumerable<LearnedApplication> Current() => _items.Where(item => item.ExpiresAt > _now());
    private void Save(List<LearnedApplication> items)
    {
        ScreenSettingsJson.Write(_path, items, MaxBytes);
        _items = items; // A failed atomic write must not alter the live memory.
    }

    private static List<LearnedApplication> Sanitize(IEnumerable<LearnedApplication?> source, DateTimeOffset now)
    {
        var valid = new List<LearnedApplication>();
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item == null) continue;
            string? app = NormalizeApp(item.App), identity = NormalizeIdentity(item.Identity), activity = NormalizeActivity(item.Activity);
            if (app == null || identity == null || activity == null || item.Id != IdFor(app, identity) ||
                !double.IsFinite(item.Confidence) || item.Confidence is < MinimumConfidence or > 1 ||
                item.LearnedAt > now.AddMinutes(5) || item.ExpiresAt <= now || item.ExpiresAt <= item.LearnedAt ||
                item.ExpiresAt - item.LearnedAt > Lifetime || !duplicates.Add(item.Id)) continue;
            valid.Add(item with { App = app, Identity = identity, Activity = activity });
        }
        return valid.OrderByDescending(item => item.UserCorrected).ThenByDescending(item => item.LearnedAt).Take(MaximumEntries).ToList();
    }

    internal static string? NormalizeApp(string? value)
    {
        string name = value?.Trim() ?? "";
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        if (name.Length is < 1 or > 80 || name is "." or ".." ||
            name.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ' or '!'))) return null;
        return name.ToLowerInvariant();
    }

    internal static string? NormalizeIdentity(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    internal static string? NormalizeActivity(string? value) => value?.Trim().ToLowerInvariant() switch
    { "work" => "work", "game" => "game", "media" => "media", "daily" => "daily", _ => null };
    private static string IdFor(string app, string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(app + "\0" + identity))).ToLowerInvariant();
}
