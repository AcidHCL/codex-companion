using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopPet;

internal static class PetVoiceLibraryChecks
{
    internal static void Run(Action<bool, string> require)
    {
        string evidence = Path.Combine(AppContext.BaseDirectory, "voice-library-evidence", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(evidence, "voices");
        var library = new PetVoiceLibrary(root);
        var sample = new PetVoiceDesignSample("gpt", "清亮优雅、略带傲娇的青年女性声音。", "你好，今天也交给本小姐吧。", "Chinese", Wave(2));
        require(library.FindLatestForSkin("gpt") == null && library.ListForSkin("gpt").Count == 0 && !Directory.Exists(root),
            "voice_library_construction_and_missing_saved_voice_lookup_do_not_create_user_state");
        PetVoiceProfile first = library.Save(sample);
        string firstPath = Path.Combine(root, first.ReferenceId!);
        require(first.ModelId == "saved-voice" && first.Instruct == "" && PetVoiceProfiles.IsReferenceId(first.ReferenceId) &&
            File.ReadAllBytes(Path.Combine(firstPath, "reference.wav")).SequenceEqual(sample.Wave) &&
            File.ReadAllText(Path.Combine(firstPath, "reference.txt")).Trim() == sample.Text,
            "saved_reference_publishes_complete_pcm_and_corresponding_text_under_opaque_id");
        require(library.TryReadMetadata(first.ReferenceId, out var metadata) && metadata!.SkinId == sample.SkinId &&
            metadata.Instruction == sample.Instruction && metadata.DurationSeconds == 2 &&
            metadata.Sha256 == Convert.ToHexString(SHA256.HashData(sample.Wave)).ToLowerInvariant(),
            "saved_voice_metadata_retains_design_and_audio_integrity");
        PetVoiceProfile second = library.Save(sample with { SkinId = "claude", Instruction = "温暖可靠的青年女性声音。" });
        require(second.ReferenceId != first.ReferenceId && File.Exists(Path.Combine(firstPath, "reference.wav")) &&
            library.TryReadMetadata(second.ReferenceId, out var other) && other!.SkinId == "claude" &&
            !Directory.GetDirectories(root).Any(x => Path.GetFileName(x).StartsWith(".pending-", StringComparison.Ordinal)),
            "saving_another_character_keeps_prior_immutable_reference_and_publishes_no_pending_folder");
        require(library.FindLatestForSkin("gpt")?.ReferenceId == first.ReferenceId &&
            library.FindLatestForSkin("claude")?.ReferenceId == second.ReferenceId && library.FindLatestForSkin("deepseek") == null,
            "saved_voice_lookup_keeps_character_histories_separate");
        PetVoiceProfile newest = library.Save(sample with { Instruction = "新的清亮优雅女性声音。" });
        // Make recency independent of the platform clock's resolution.
        library.TryReadMetadata(newest.ReferenceId, out var newestInfo);
        File.WriteAllText(Path.Combine(root, newest.ReferenceId!, "metadata.json"),
            JsonSerializer.Serialize(newestInfo! with { CreatedUtc = metadata!.CreatedUtc.AddMinutes(1) }));
        require(new PetVoiceLibrary(root).FindLatestForSkin("GPT")?.ReferenceId == newest.ReferenceId &&
            new PetVoiceLibrary(root).FindLatestForSkin("claude")?.ReferenceId == second.ReferenceId,
            "fresh_library_restores_latest_character_voice_after_profile_switch_or_restart");
        int savedCount = Directory.GetDirectories(root).Length;
        require(Rejected(() => library.Save(sample with { Wave = sample.Wave[..^1] })) &&
            Directory.GetDirectories(root).Length == savedCount && library.TryReadMetadata(first.ReferenceId, out _),
            "failed_reference_validation_leaves_previous_versions_untouched");
        var malformedStereo = Wave(2); BinaryPrimitives.WriteUInt16LittleEndian(malformedStereo.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(malformedStereo.AsSpan(28), 96000); BinaryPrimitives.WriteUInt16LittleEndian(malformedStereo.AsSpan(32), 4);
        var wrongRate = Wave(2); BinaryPrimitives.WriteUInt32LittleEndian(wrongRate.AsSpan(24), 48000); BinaryPrimitives.WriteUInt32LittleEndian(wrongRate.AsSpan(28), 96000);
        require(Rejected(() => PetVoiceLibrary.ValidateWave(malformedStereo)) && Rejected(() => PetVoiceLibrary.ValidateWave(wrongRate)) &&
            Rejected(() => PetVoiceLibrary.ValidateWave(Wave(.5))) && Rejected(() => PetVoiceLibrary.ValidateWave(Wave(31))) &&
            Rejected(() => PetVoiceLibrary.ValidateWave(new byte[PetVoiceLibrary.MaximumWaveBytes + 1])),
            "reference_audio_rejects_wrong_format_short_long_or_oversized_waveforms");
        require(Rejected(() => library.Save(sample with { SkinId = "../gpt" })) &&
            Rejected(() => library.Save(sample with { Instruction = new string('字', 601) })) &&
            Rejected(() => library.Save(sample with { Text = new string('字', 101) })) &&
            Rejected(() => library.Save(sample with { Instruction = "坏\0描述" })) &&
            Rejected(() => library.Save(sample with { Language = "unknown" })),
            "reference_inputs_reject_paths_oversized_text_controls_and_unsupported_language");
        require(!library.TryReadMetadata("../voices", out _) && !library.TryReadMetadata(new string('a', 32).ToUpperInvariant(), out _) &&
            !library.TryReadMetadata(new string('a', 31), out _),
            "metadata_reads_reject_path_and_noncanonical_reference_identifiers");
        string blockedRoot = Path.Combine(evidence, "blocked-root");
        File.WriteAllText(blockedRoot, "fixture");
        require(Rejected(() => new PetVoiceLibrary(blockedRoot).Save(sample)) && library.TryReadMetadata(first.ReferenceId, out _),
            "disk_publish_failure_does_not_replace_existing_voice");
        string metadataPath = Path.Combine(firstPath, "metadata.json");
        var originalMetadata = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata! with { ReferenceId = second.ReferenceId! }));
        require(!library.TryReadMetadata(first.ReferenceId, out _), "metadata_must_match_the_selected_immutable_reference_id");
        File.WriteAllText(metadataPath, originalMetadata);

        var legacy = JsonNode.Parse(originalMetadata)!.AsObject(); legacy.Remove("Name");
        string legacyJson = legacy.ToJsonString(); File.WriteAllText(metadataPath, legacyJson);
        require(library.TryReadMetadata(first.ReferenceId, out var legacyRead) &&
            legacyRead!.Name.Contains("GPT") && !string.IsNullOrWhiteSpace(legacyRead.Name) &&
            File.ReadAllText(metadataPath) == legacyJson,
            "unnamed_legacy_voice_gets_a_readable_label_without_mutating_metadata_or_audio");

        string firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(firstPath, "reference.wav"))));
        var renamed = library.Rename(first.ReferenceId!, "  温润小龙 · 日常  ");
        require(renamed.Name == "温润小龙 · 日常" && renamed.ReferenceId == first.ReferenceId &&
            library.TryReadMetadata(first.ReferenceId, out var renamedRead) && renamedRead!.Name == renamed.Name &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(firstPath, "reference.wav")))) == firstHash &&
            File.ReadAllText(Path.Combine(firstPath, "reference.txt")).Trim() == sample.Text &&
            !Directory.EnumerateFiles(firstPath).Any(path => Path.GetFileName(path).StartsWith(".metadata-", StringComparison.Ordinal)),
            "rename_atomically_updates_only_name_preserving_reference_id_audio_and_text");

        var named = library.Save(sample, "清亮小龙");
        var duplicateName = library.Save(sample, "清亮小龙");
        var gptVoices = library.ListForSkin("gpt");
        require(named.ReferenceId != duplicateName.ReferenceId &&
            gptVoices.Count(item => item.Name == "清亮小龙") == 2 &&
            gptVoices.All(item => item.SkinId == "gpt") && library.ListForSkin("claude").Single().ReferenceId == second.ReferenceId &&
            gptVoices.Zip(gptVoices.Skip(1)).All(pair => pair.First.CreatedUtc >= pair.Second.CreatedUtc),
            "named_voice_list_keeps_duplicate_labels_as_distinct_ids_and_separates_character_histories");

        string unchangedMetadata = File.ReadAllText(metadataPath);
        int voiceCount = library.ListForSkin("gpt").Count;
        foreach (string invalidName in new[] { "", "   ", "a\nb", "a\tb", "a\u2028b", new string('字', 61), "\ud800", ".", ".." })
            require(Rejected(() => library.Save(sample, invalidName)) && Rejected(() => library.Rename(first.ReferenceId!, invalidName)),
                "invalid_voice_name_is_rejected_before_save_or_rename");
        require(library.ListForSkin("gpt").Count == voiceCount && File.ReadAllText(metadataPath) == unchangedMetadata,
            "invalid_name_operations_leave_existing_voice_library_unchanged");

        const string displayOnlyName = "../outside/name: 12:34 | 龙娘";
        var displayOnly = library.Save(sample, displayOnlyName);
        require(PetVoiceProfiles.IsReferenceId(displayOnly.ReferenceId) &&
            library.TryReadMetadata(displayOnly.ReferenceId, out var displayRead) && displayRead!.Name == displayOnlyName &&
            Directory.Exists(Path.Combine(root, displayOnly.ReferenceId!)) && !Directory.Exists(Path.Combine(evidence, "outside")) &&
            !File.Exists(Path.Combine(evidence, "outside")),
            "name_punctuation_and_path_like_text_remain_metadata_under_random_reference_directory");

        using (var blockedMetadata = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.None))
            require(Rejected(() => library.Rename(first.ReferenceId!, "无法写入的名称")), "locked_metadata_cannot_be_silently_overwritten");
        require(File.ReadAllText(metadataPath) == unchangedMetadata &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(firstPath, "reference.wav")))) == firstHash,
            "failed_rename_keeps_previous_metadata_and_audio");

        foreach (string protectedId in new[] { "base", "gpt-fixed", "deepseek-let-me-go", "gpt-dragon-fixed-v1", "../outside", new string('a', 31), new string('A', 32) })
            require(Rejected(() => library.Delete(protectedId)) && Rejected(() => library.Rename(protectedId, "不能修改默认音色")),
                "default_voice_ids_and_path_inputs_cannot_be_renamed_or_removed");

        string namedPath = Path.Combine(root, named.ReferenceId!);
        byte[] namedWave = File.ReadAllBytes(Path.Combine(namedPath, "reference.wav"));
        library.Delete(named.ReferenceId!);
        string trash = Path.Combine(root, ".trash-" + named.ReferenceId);
        require(!Directory.Exists(namedPath) && !library.TryReadMetadata(named.ReferenceId, out _) &&
            !library.ListForSkin("gpt").Any(item => item.ReferenceId == named.ReferenceId) &&
            File.ReadAllBytes(Path.Combine(trash, "reference.wav")).SequenceEqual(namedWave) &&
            File.Exists(Path.Combine(trash, "metadata.json")) && File.Exists(Path.Combine(trash, "reference.txt")) &&
            library.TryReadMetadata(first.ReferenceId, out _) && library.TryReadMetadata(second.ReferenceId, out _),
            "delete_atomically_removes_only_selected_custom_voice_and_preserves_recoverable_bytes");

        string collision = Path.Combine(root, ".trash-" + duplicateName.ReferenceId);
        Directory.CreateDirectory(collision);
        require(Rejected(() => library.Delete(duplicateName.ReferenceId!)) && library.TryReadMetadata(duplicateName.ReferenceId, out _) &&
            File.Exists(Path.Combine(root, duplicateName.ReferenceId!, "reference.wav")),
            "failed_soft_delete_does_not_overwrite_recovery_copy_or_remove_original_voice");
    }

    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { return true; }
    }

    private static byte[] Wave(double seconds)
    {
        int samples = (int)(24000 * seconds);
        var wave = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 24000);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), (uint)(samples * 2));
        for (int i = 0; i < samples; i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 24000) * 4000));
        return wave;
    }
}
