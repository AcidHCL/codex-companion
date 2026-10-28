using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DesktopPet;

// Every file is under the current validation executable, never the user's real AIPet data.
internal static class ChatMemoryStoreChecks
{
    public static object Run()
    {
        var checks = new List<object>();
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Chat database check failed: " + name);
        }
        bool Reject(Action action)
        {
            try { action(); return false; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ChatMemoryStoreException) { return true; }
        }
        string root = Path.Combine(AppContext.BaseDirectory, "chat-memory-store-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "conversation.db");
        const string local = "http://127.0.0.1:11434";
        const string remote = "http://192.0.2.12:11434";
        const string persona = "deepseek";
        const string user = "PRIVATE_USER_NOTE_783719_THIS_MUST_NOT_BE_PLAINTEXT";
        const string assistant = "PRIVATE_ASSISTANT_REPLY_537001_THIS_MUST_NOT_BE_PLAINTEXT";
        const string memoryContent = "PRIVATE_MEMORY_427159_THIS_MUST_NOT_BE_PLAINTEXT";
        const string draft = "PRIVATE_DRAFT_981263_THIS_MUST_NOT_BE_PLAINTEXT";
        const string title = "PRIVATE_TITLE_156799";
        // One valid tiny PNG; tests do not touch the user's actual images.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jh4sAAAAASUVORK5CYII=";
        var images = new[] { new ChatStoredImage("sample.png", png) };
        var sources = new[] { new PetWebSource("PRIVATE_SOURCE_TITLE", "https://example.com/verified", "TRANSIENT_WEB_BODY_NOT_FOR_MEMORY") };
        string firstId, secondId, otherId, remoteId, memoryId;
        DateTimeOffset created;
        using (var store = new ChatMemoryStore(database))
        {
            Check(store.ListConversations(local, persona).Count == 0 && store.ListMemories(local).Count == 0, "fresh_database_is_empty");
            var first = store.CreateConversation(local, persona, OllamaModels.FastModel);
            firstId = first.Id; created = first.CreatedUtc;
            Check(first.Title == "新对话" && first.Draft.Length == 0 && first.DraftImages.Count == 0, "new_conversation_has_generated_id_and_empty_draft");
            Check(store.GetActiveConversationId(local + "/", persona) == firstId, "canonical_endpoint_and_new_active_pointer");
            var second = store.CreateConversation(local, persona, OllamaModels.QualityModel); secondId = second.Id;
            var other = store.CreateConversation(local, "gemini", OllamaModels.FastModel); otherId = other.Id;
            var remoteConversation = store.CreateConversation(remote, persona, OllamaModels.FastModel); remoteId = remoteConversation.Id;
            Check(store.ListConversations(local, persona).Count == 2 && store.ListConversations(local, "gemini").Single().Id == otherId && store.ListConversations(remote, persona).Single().Id == remoteId, "conversation_lists_isolate_persona_and_server");
            store.SetActiveConversation(firstId);
            Check(store.GetActiveConversationId(local, persona) == firstId && store.GetActiveConversationId(local, "gemini") == otherId && store.GetActiveConversationId(remote, persona) == remoteId, "active_conversation_is_independent_for_every_scope");
            store.SaveDraft(firstId, draft, images);
            var savedDraft = store.GetConversation(firstId)!;
            Check(savedDraft.Draft == draft && savedDraft.DraftImages.Single().Base64 == png && savedDraft.HasDraft, "draft_and_attachment_round_trip");
            var summary = store.ListConversations(local, persona).Single(item => item.Id == firstId);
            Check(summary.HasDraft && summary.Draft.Length == 0 && summary.DraftImages.Count == 0, "library_list_omits_large_draft_payloads");
            var turn = new ChatStoredTurn(0, user, assistant, OllamaModels.FastModel, images, sources, "Test warning", default);
            long sequence = store.AppendTurn(firstId, turn);
            var persisted = store.ReadTurns(firstId).Single();
            Check(sequence == 1 && persisted.Sequence == 1 && persisted.User == user && persisted.Assistant == assistant && persisted.Model == OllamaModels.FastModel, "complete_turn_committed_with_assigned_sequence");
            Check(persisted.Images.Single().Base64 == png && persisted.Sources.Single().Url == sources[0].Url && persisted.WebWarning == "Test warning", "turn_images_sources_and_warning_survive");
            Check(persisted.Sources.Single().Text.Length == 0, "retrieved_page_body_is_not_archived_as_long_term_memory");
            var completed = store.GetConversation(firstId)!;
            Check(completed.Draft.Length == 0 && completed.DraftImages.Count == 0 && !completed.HasDraft && completed.Title == user, "turn_atomically_clears_draft_and_generates_title");
            store.RenameConversation(firstId, title);
            store.SaveDraft(firstId, draft, images);
            Check(Reject(() => store.AppendTurn(firstId, turn with { Assistant = "" })) && store.ReadTurns(firstId).Count == 1 && store.GetConversation(firstId)!.Draft == draft, "invalid_turn_preserves_history_and_draft_atomically");
            Check(Reject(() => store.AppendTurn(firstId, turn with { Images = new[] { new ChatStoredImage("x", "a=b=") } })) && store.ReadTurns(firstId).Count == 1, "invalid_image_encoding_cannot_partially_commit");
            Check(Reject(() => store.AppendTurn(firstId, turn with { Sources = new[] { new PetWebSource("x", "file:///private.txt", "") } })), "non_web_source_link_is_rejected");
            for (int index = 2; index <= 25; index++)
                Check(store.AppendTurn(firstId, turn with { Sequence = 999, User = "User " + index, Assistant = "Reply " + index, Images = Array.Empty<ChatStoredImage>(), Sources = Array.Empty<PetWebSource>() }) == index, "sequential_atomic_archive_turn_" + index);
            Check(store.GetConversation(firstId)!.Title == title && store.ReadTurns(firstId).Count == 25, "manual_title_is_preserved_and_archive_not_trimmed_to_context");
            var latest = store.ReadTurns(firstId, 10);
            var middle = store.ReadTurns(firstId, 10, latest[0].Sequence);
            var oldest = store.ReadTurns(firstId, 10, middle[0].Sequence);
            Check(latest.Select(item => item.Sequence).SequenceEqual(Enumerable.Range(16, 10).Select(i => (long)i)) && middle[0].Sequence == 6 && oldest[0].Sequence == 1 && oldest.Count == 5, "history_paging_has_no_duplicates_gaps_or_reverse_order");
            Check(store.ReadTurns(firstId, 10, 1).Count == 0 && Reject(() => store.ReadTurns(firstId, 101)), "paging_bounds_are_checked");
            Check(store.ReadTurns(secondId).Count == 0 && store.ReadTurns(otherId).Count == 0 && store.ReadTurns(remoteId).Count == 0, "turn_history_is_private_to_selected_conversation");
            var memory = store.UpsertMemory(new ChatSharedMemory("", local, "PRIVATE_MEMORY_TITLE", memoryContent, true, default)); memoryId = memory.Id;
            Check(memory.Id.Length == 32 && memory.UpdatedUtc != default && store.ListMemories(local).Single() == memory, "shared_memory_create_generates_id_and_timestamp");
            Check(store.ListMemories(remote).Count == 0, "shared_memories_never_cross_server_scope");
            store.SetActiveConversation(otherId);
            Check(store.ListMemories(local).Single().Content == memoryContent, "shared_memory_is_available_across_personas_on_same_server");
            var updated = store.UpsertMemory(memory with { Title = "Updated memory", Pinned = false });
            Check(store.ListMemories(local).Single().Title == "Updated memory" && !updated.Pinned, "memory_edit_updates_single_existing_entry");
            Check(Reject(() => store.UpsertMemory(updated with { Endpoint = remote })) && store.ListMemories(local).Count == 1 && store.ListMemories(remote).Count == 0, "upsert_cannot_move_private_memory_to_another_server");
            Check(Reject(() => store.UpsertMemory(updated with { Content = new string('x', 1001) })) && store.ListMemories(local).Single().Content == memoryContent, "invalid_memory_edit_retains_previous_content");
            Check(Reject(() => store.CreateConversation("https://example.com/api/chat", persona, OllamaModels.FastModel)) && store.ListConversations(local, persona).Count == 2, "invalid_endpoint_never_silently_falls_back_to_local_scope");
            store.SaveDraft(secondId, draft, images);
            store.SetActiveConversation(firstId);
            foreach (string filename in Directory.GetFiles(root, "conversation.db*"))
            {
                // SQLite keeps write-capable database/WAL handles open. The test's
                // observer must allow those handles; File.ReadAllBytes shares only reads.
                using var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var captured = new MemoryStream();
                file.CopyTo(captured);
                string bytes = Encoding.Latin1.GetString(captured.ToArray());
                Check(new[] { user, assistant, draft, title, memoryContent, "PRIVATE_MEMORY_TITLE", "PRIVATE_SOURCE_TITLE", png, "TRANSIENT_WEB_BODY_NOT_FOR_MEMORY" }.All(value => !bytes.Contains(value, StringComparison.Ordinal)), "sensitive_fields_are_ciphertext_in_" + Path.GetFileName(filename));
            }
        }
        using (var reopened = new ChatMemoryStore(database))
        {
            Check(reopened.GetConversation(firstId)!.CreatedUtc == created && reopened.GetConversation(firstId)!.Title == title && reopened.ReadTurns(firstId).Count == 25, "reopen_recovers_complete_archive_and_metadata");
            Check(reopened.GetConversation(secondId)!.Draft == draft && reopened.GetConversation(secondId)!.DraftImages.Single().Base64 == png, "reopen_recovers_unsent_draft_and_attachment");
            Check(reopened.GetActiveConversationId(local, persona) == firstId && reopened.ListMemories(local).Single().Content == memoryContent, "reopen_recovers_selection_and_shared_memory");
            reopened.DeleteConversation(firstId);
            Check(reopened.GetConversation(firstId) == null && reopened.ReadTurns(firstId).Count == 0 && reopened.GetActiveConversationId(local, persona) == null, "delete_cascades_turns_and_active_pointer");
            Check(reopened.ListMemories(local).Single().Id == memoryId && reopened.GetConversation(secondId) != null && reopened.GetConversation(remoteId) != null, "deleting_one_conversation_preserves_shared_memory_and_other_sessions");
            reopened.DeleteMemory(memoryId);
            Check(reopened.ListMemories(local).Count == 0, "explicit_memory_delete_removes_entry");
        }
        using (var isolated = new ChatMemoryStore())
            Check(isolated.DatabasePath == ":memory:" && isolated.ListConversations(local, persona).Count == 0, "default_store_is_safe_isolated_memory_database");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check;";
            Check((string?)command.ExecuteScalar() == "ok", "database_integrity_after_transactions_and_deletions");
            command.CommandText = "SELECT COUNT(*) FROM turns WHERE conversation_id=$id;";
            command.Parameters.AddWithValue("$id", firstId);
            Check(Convert.ToInt64(command.ExecuteScalar()) == 0, "foreign_key_delete_removes_underlying_turn_rows");
        }
        string invalid = Path.Combine(root, "corrupt.db");
        var original = Encoding.UTF8.GetBytes("Not a database. PRIVATE_CORRUPT_FILE_MUST_BE_PRESERVED");
        File.WriteAllBytes(invalid, original);
        Check(Reject(() => { using var ignored = new ChatMemoryStore(invalid); }) && File.ReadAllBytes(invalid).SequenceEqual(original), "corrupt_file_reports_error_without_reset_or_replacement");
        string future = Path.Combine(root, "future.db");
        using (var futureStore = new ChatMemoryStore(future)) { _ = futureStore.CreateConversation(local, persona, OllamaModels.FastModel); }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = future, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=999;"; command.ExecuteNonQuery();
        }
        var futureOriginal = File.ReadAllBytes(future);
        Check(Reject(() => { using var ignored = new ChatMemoryStore(future); }) && File.ReadAllBytes(future).SequenceEqual(futureOriginal), "newer_schema_is_rejected_without_destructive_migration");
        string damagedPayload = Path.Combine(root, "damaged-payload.db"); string damagedId;
        using (var good = new ChatMemoryStore(damagedPayload)) { damagedId = good.CreateConversation(local, persona, OllamaModels.FastModel).Id; }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = damagedPayload, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE conversations SET title=$invalid;"; command.Parameters.AddWithValue("$invalid", new byte[] { 1, 2, 3 }); command.ExecuteNonQuery();
        }
        using (var damaged = new ChatMemoryStore(damagedPayload))
            Check(Reject(() => damaged.GetConversation(damagedId)) && File.Exists(damagedPayload), "failed_decryption_reports_error_without_erasing_database");
        Check(Path.GetFullPath(database).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase) && !string.Equals(database, ChatMemoryStore.DefaultPath, StringComparison.OrdinalIgnoreCase), "all_tests_are_isolated_from_real_user_database_and_pairing");
        return new { passed = true, assertions = checks.Count, database, evidence = root, userDataUntouched = true, checks };
    }
}
