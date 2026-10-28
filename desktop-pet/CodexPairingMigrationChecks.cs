using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

// Fabricated records only, in an explicitly supplied unique workspace directory.
// No real user state, official app, named pipe, or message body is accessed.
internal static class CodexPairingMigrationChecks
{
    internal static object Run(string temporaryParent)
    {
        string directory = Path.Combine(Path.GetFullPath(temporaryParent), "pairing-migration-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("Pairing migration check failed: " + name);
            checks.Add(name);
        }
        CodexConnectionStore Store(string name) => new(Path.Combine(directory, name));
        CodexConnectionHint Hint() => new(@"\\.\pipe\codex-browser-use-" + Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow.AddDays(-4));
        bool HasState(CodexConnectionStore store) => File.Exists(store.ProtectedPath) ||
            File.Exists(store.LegacyPath) || File.Exists(store.RevocationPath) || File.Exists(store.MigrationSealPath);
        string PrivateAcl(CodexConnectionStore store) => new FileInfo(store.ProtectedPath).GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        try
        {
            var source = Store("candidate-source");
            var target = Store("candidate-target");
            var original = Hint();
            Check(source.SaveVerified(original), "prepare_private_encrypted_old_scope");
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source.ProtectedPath));
            string sourceAcl = PrivateAcl(source);
            var migration = new CodexPairingMigration(target, source, () => true);
            var loaded = migration.Load();
            Check(loaded.Hint == original && loaded.State == CodexPairingState.Ready && loaded.IsMigrationCandidate,
                "only_ready_encrypted_source_and_missing_target_produce_candidate");
            Check(!HasState(target), "candidate_does_not_write_credentials_or_seal_before_peer_verification");
            Check(migration.Load().Hint == original && !HasState(target), "offline_retry_keeps_uncommitted_candidate_available");
            var verified = Hint() with { SavedAtUtc = DateTimeOffset.UtcNow };
            Check(migration.CommitVerified(original, verified), "verified_candidate_commits_without_reactivation");
            Check(target.Load() == (verified with { SavedAtUtc = original.SavedAtUtc }),
                "commit_uses_verified_endpoint_and_context_but_preserves_original_saved_time");
            Check(target.TryReadMigrationSeal(out bool committedSeal) && committedSeal,
                "verified_commit_closes_old_scope_with_private_seal");
            Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source.ProtectedPath))) && sourceAcl == PrivateAcl(source),
                "migration_preserves_source_ciphertext_and_acl_exactly");
            var repeated = migration.Load();
            Check(!repeated.IsMigrationCandidate && repeated.Hint == target.Load(), "subsequent_load_uses_canonical_record_only");
            File.Delete(target.ProtectedPath); // synthetic loss after successful migration
            var lost = migration.Load();
            Check(lost.State == CodexPairingState.Missing && lost.Hint == null && !lost.IsMigrationCandidate,
                "deleted_canonical_record_never_reimports_old_pairing");
            Check(!migration.CommitVerified(original, verified) && !File.Exists(target.ProtectedPath),
                "second_commit_cannot_bypass_one_time_seal");

            var existingTarget = Store("existing-target");
            var existingHint = Hint();
            Check(existingTarget.SaveVerified(existingHint), "prepare_independent_canonical_pairing");
            byte[] existingHash = SHA256.HashData(File.ReadAllBytes(existingTarget.ProtectedPath));
            var existingMigration = new CodexPairingMigration(existingTarget, source, () => true);
            using (var held = new FileStream(source.ProtectedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(existingMigration.Load().Hint == existingHint, "existing_target_wins_without_reading_locked_source");
            Check(existingHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(existingTarget.ProtectedPath))),
                "existing_target_ciphertext_is_not_overwritten");
            File.Delete(existingTarget.ProtectedPath);
            Check(existingMigration.Load().State == CodexPairingState.Missing,
                "previously_existing_target_also_closes_legacy_fallback");

            var revokedTarget = Store("revoked-target");
            revokedTarget.Forget();
            var targetRevocation = new CodexPairingMigration(revokedTarget, source, () => true);
            using (var held = new FileStream(source.ProtectedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(targetRevocation.Load().State == CodexPairingState.Revoked,
                    "canonical_revocation_has_priority_without_reading_source");
            Check(!targetRevocation.CommitVerified(original, verified) && revokedTarget.IsRevoked &&
                !File.Exists(revokedTarget.ProtectedPath), "migration_never_reactivates_revoked_target");

            var revokedSource = Store("revoked-source");
            revokedSource.Forget();
            var inheritTarget = Store("inherit-target");
            var inherit = new CodexPairingMigration(inheritTarget, revokedSource, () => true);
            Check(inherit.Load().State == CodexPairingState.Revoked && inheritTarget.IsRevoked,
                "old_scope_revocation_is_inherited_by_missing_canonical_store");
            Check(inheritTarget.TryReadMigrationSeal(out bool inheritedSeal) && inheritedSeal &&
                !File.Exists(inheritTarget.ProtectedPath), "inherited_revocation_seals_fallback_without_credential_copy");

            var emptySource = Store("empty-source");
            var emptyTarget = Store("empty-target");
            var emptyMigration = new CodexPairingMigration(emptyTarget, emptySource, () => true);
            Check(emptyMigration.Load().State == CodexPairingState.Missing, "fresh_install_remains_unpaired");
            Check(emptySource.SaveVerified(Hint()), "prepare_late_old_scope_record");
            Check(emptyMigration.Load().State == CodexPairingState.Missing,
                "record_appearing_after_fresh_install_cannot_enable_legacy_fallback");

            var invalidSource = Store("invalid-source");
            Check(invalidSource.SaveVerified(Hint()), "prepare_invalid_ciphertext_fixture");
            File.WriteAllBytes(invalidSource.ProtectedPath, new byte[] { 1, 2, 3 });
            var invalidTarget = Store("invalid-target");
            Check(new CodexPairingMigration(invalidTarget, invalidSource, () => true).Load().State == CodexPairingState.Invalid &&
                !HasState(invalidTarget), "invalid_source_is_rejected_without_target_mutation");

            var unsafeSource = Store("unsafe-source");
            Check(unsafeSource.SaveVerified(Hint()), "prepare_unsafe_acl_fixture");
            var broadAcl = new FileInfo(unsafeSource.ProtectedPath).GetAccessControl();
            broadAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(unsafeSource.ProtectedPath).SetAccessControl(broadAcl);
            var unsafeTarget = Store("unsafe-target");
            Check(new CodexPairingMigration(unsafeTarget, unsafeSource, () => true).Load().State == CodexPairingState.Invalid &&
                !HasState(unsafeTarget), "unsafe_source_acl_is_rejected_without_repairing_or_copying_it");

            var plainSource = Store("plain-source");
            Directory.CreateDirectory(plainSource.RootDirectory);
            var plain = Hint();
            File.WriteAllText(plainSource.LegacyPath, JsonSerializer.Serialize(new { version = 1, endpoint = plain.Endpoint,
                callerThreadId = plain.CallerThreadId, callerTurnId = plain.CallerTurnId, savedAtUtc = plain.SavedAtUtc }));
            var plainTarget = Store("plain-target");
            Check(new CodexPairingMigration(plainTarget, plainSource, () => true).Load().State == CodexPairingState.Invalid &&
                !HasState(plainTarget), "plaintext_legacy_source_requires_explicit_pairing_instead_of_migration");

            var expiredSource = Store("expired-source");
            var fresh = Hint();
            Check(expiredSource.SaveVerified(fresh), "prepare_encrypted_expiry_fixture");
            byte[] expiredBytes = Encoding.ASCII.GetBytes("AIPETC02").Concat(CodexConnectionStore.ProtectForCurrentUser(
                JsonSerializer.SerializeToUtf8Bytes(fresh with { SavedAtUtc = DateTimeOffset.UtcNow.AddDays(-91) }))).ToArray();
            File.WriteAllBytes(expiredSource.ProtectedPath, expiredBytes);
            var expiredTarget = Store("expired-target");
            Check(new CodexPairingMigration(expiredTarget, expiredSource, () => true).Load().State == CodexPairingState.Expired &&
                !HasState(expiredTarget), "expired_source_cannot_gain_new_lifetime_through_migration");

            var lockedTarget = Store("locked-target");
            using (var held = new FileStream(source.ProtectedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(new CodexPairingMigration(lockedTarget, source, () => true).Load().State == CodexPairingState.Unavailable &&
                    !HasState(lockedTarget), "locked_old_scope_is_retryable_without_silent_pairing_or_revocation");

            var uncertainTarget = Store("uncertain-host-target");
            Check(new CodexPairingMigration(uncertainTarget, source, () => false).Load().State == CodexPairingState.Missing &&
                !HasState(uncertainTarget), "uncertain_host_cannot_read_or_import_old_scope");
            using (var isolated = new CodexConnection(store: uncertainTarget))
                Check(!isolated.HasSavedPairing && isolated.CandidateAttempts == 0,
                    "ordinary_injected_fixture_store_never_consults_real_user_migration_paths");

            var racedTarget = Store("raced-target");
            var racedMigration = new CodexPairingMigration(racedTarget, source, () => true);
            Check(racedMigration.Load().IsMigrationCandidate, "prepare_candidate_before_concurrent_target_revocation");
            racedTarget.Forget();
            Check(!racedMigration.CommitVerified(original, verified) && racedTarget.IsRevoked,
                "revocation_between_candidate_read_and_commit_wins");

            var lateSource = Store("late-revoked-source");
            var lateHint = Hint();
            Check(lateSource.SaveVerified(lateHint), "prepare_candidate_before_concurrent_source_revocation");
            var lateTarget = Store("late-revoked-target");
            var lateMigration = new CodexPairingMigration(lateTarget, lateSource, () => true);
            Check(lateMigration.Load().IsMigrationCandidate, "read_candidate_before_source_revocation");
            lateSource.Forget();
            Check(!lateMigration.CommitVerified(lateHint, lateHint) && lateTarget.IsRevoked,
                "source_revocation_during_verification_is_inherited_instead_of_committing");

            var changedSource = Store("changed-source");
            var beforeChange = Hint();
            Check(changedSource.SaveVerified(beforeChange), "prepare_source_change_fixture");
            var changedTarget = Store("changed-target");
            var changedMigration = new CodexPairingMigration(changedTarget, changedSource, () => true);
            Check(changedMigration.Load().IsMigrationCandidate && changedSource.SaveVerified(Hint()),
                "source_changes_after_candidate_read");
            Check(!changedMigration.CommitVerified(beforeChange, beforeChange) && !HasState(changedTarget),
                "changed_source_is_not_committed_using_obsolete_verification");

            var badSealTarget = Store("bad-seal-target");
            Check(badSealTarget.TrySealLegacyMigration(), "prepare_seal_integrity_fixture");
            File.WriteAllText(badSealTarget.MigrationSealPath, "invalid seal");
            Check(new CodexPairingMigration(badSealTarget, source, () => true).Load().State == CodexPairingState.Invalid &&
                !File.Exists(badSealTarget.ProtectedPath), "malformed_seal_blocks_legacy_fallback");

            return new { passed = true, assertions = checks.Count, checks, syntheticStoresOnly = true,
                realAccountRequests = 0, realPipeDiscoveryAttempts = 0, sourceCiphertextAndAclUnchanged = true };
        }
        finally
        {
            // Exact unique directory created above, under the caller's test root.
            Directory.Delete(directory, recursive: true);
        }
    }
}
