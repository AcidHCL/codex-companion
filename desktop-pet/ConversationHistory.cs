using System;
using System.Linq;

namespace DesktopPet;

// Merge only the public, chronological messages supplied by CodexParser.
// The monitor owns cancellation, selection, and transport; these functions are offline.
internal static class ConversationHistory
{
    public static (ConversationPage Page, bool OlderLoaded) MergeLatest(
        ConversationPage? current, ConversationPage latest, bool olderLoaded)
    {
        var latestIds = latest.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        bool overlaps = current?.TaskKey == latest.TaskKey && current.Messages.Any(message => latestIds.Contains(message.Id));
        // Without overlap, the latest limited page may have skipped unseen turns.
        // Start a fresh contiguous chain so its cursor can recover the missing history.
        if (!overlaps || current == null) return (latest, false);

        var messages = current.Messages.Where(message => !latestIds.Contains(message.Id))
            .Concat(latest.Messages).ToArray();
        return (latest with
        {
            Messages = messages,
            NextCursor = olderLoaded ? current.NextCursor : latest.NextCursor,
            HasMore = olderLoaded ? current.HasMore : latest.HasMore
        }, olderLoaded);
    }

    public static ConversationPage MergeOlder(
        ConversationPage current, ConversationPage older, string requestedCursor)
    {
        if (current.TaskKey != older.TaskKey)
            throw new InvalidOperationException("Cannot merge history from different Codex tasks.");
        var existingIds = current.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var messages = older.Messages.Where(message => !existingIds.Contains(message.Id))
            .Concat(current.Messages).ToArray();
        return current with
        {
            Messages = messages,
            NextCursor = older.NextCursor,
            HasMore = older.HasMore && older.NextCursor != requestedCursor
        };
    }
}
