using System;
using System.IO;

namespace DesktopPet;

internal readonly record struct CodexPairingLoad(CodexConnectionHint? Hint, CodexPairingState State,
    bool IsMigrationCandidate = false);

// Old versions launched inside Codex's desktop-app runtime could write a private
// AppData overlay. Only an independent process may consider that one known old
// location, and only before the canonical store has ever acquired its own state.
internal sealed class CodexPairingMigration
{
    private readonly CodexConnectionStore target;
    private readonly CodexConnectionStore source;
    private readonly Func<bool> isIndependentContext;

    // Explicit stores are used by isolated regression tests. Merely constructing
    // a CodexConnection with a fixture store never enables production migration.
    internal CodexPairingMigration(CodexConnectionStore target, CodexConnectionStore source,
        Func<bool> isIndependentContext)
    {
        this.target = target;
        this.source = source;
        this.isIndependentContext = isIndependentContext;
        if (string.Equals(target.RootDirectory, source.RootDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Migration requires distinct stores.");
    }

    internal static CodexPairingMigration CreateDefault(CodexConnectionStore target)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string canonical = Path.GetFullPath(Path.Combine(local, "AIPet"));
        string legacy = Path.Combine(local, "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Local", "AIPet");
        return new CodexPairingMigration(target, new CodexConnectionStore(legacy), () =>
            string.Equals(target.RootDirectory, canonical, StringComparison.OrdinalIgnoreCase) &&
            IndependentProcessLauncher.IsIndependentContext());
    }

    internal CodexPairingLoad Load()
    {
        try
        {
            // Never inspect an old scope from an uncertain or virtualized host.
            // The launcher normally makes this true before any store is opened.
            if (!isIndependentContext()) return ReadCanonical();
            using (target.LockForMigration())
            {
                var current = ReadCanonical();
                if (current.State != CodexPairingState.Missing) return CloseExistingScope(current);
                if (!target.TryReadMigrationSeal(out bool closed)) return Failure();
                if (closed) return current;
            }

            using var both = LockBoth();
            // Recheck after locking both stores: a concurrent explicit pairing or
            // revoke must take priority over a previously observed missing file.
            var canonical = ReadCanonical();
            if (canonical.State != CodexPairingState.Missing) return CloseExistingScope(canonical);
            if (!target.TryReadMigrationSeal(out bool sealedScope)) return Failure();
            if (sealedScope) return canonical;

            var hint = source.Load();
            if (source.LastState == CodexPairingState.Revoked)
            {
                target.Forget();
                if (!target.TrySealLegacyMigration()) return Failure();
                return new(null, CodexPairingState.Revoked);
            }
            if (source.LastState == CodexPairingState.Missing)
            {
                // Fresh installs must never later import a stale record that
                // appears in the legacy scope after an explicit new pairing.
                if (!target.TrySealLegacyMigration()) return Failure();
                return canonical;
            }
            if (source.LastState != CodexPairingState.Ready || hint == null)
                return new(null, source.LastState);
            // Only already encrypted v2 records are eligible. Plaintext legacy
            // input retains the normal explicit pairing flow and cannot migrate.
            if (!source.ProtectedRecordExistsForMigration())
                return new(null, CodexPairingState.Invalid);
            // No credentials or migration marker are written at this stage.
            // The normal signed-peer + read-only-protocol check still runs first.
            return new(hint, CodexPairingState.Ready, IsMigrationCandidate: true);
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            target.RecordMigrationFailure(error);
            return Failure();
        }
    }

    // This entry is called only after CodexPipeTrust and the usage-only protocol
    // verification succeed for this candidate. It never clears a revoke marker.
    internal bool CommitVerified(CodexConnectionHint candidate, CodexConnectionHint verified)
    {
        try
        {
            if (!isIndependentContext()) throw new UnauthorizedAccessException();
            using var both = LockBoth();
            var current = ReadCanonical();
            if (current.State != CodexPairingState.Missing)
            {
                // Even another valid record must not be overwritten with the old
                // context. A subsequent normal retry will use that newer record.
                if (current.State == CodexPairingState.Ready)
                    target.RecordMigrationFailure(new IOException("Canonical pairing changed during migration."));
                return false;
            }
            if (!target.TryReadMigrationSeal(out bool closed)) return false;
            if (closed)
            {
                target.RecordMigrationFailure(new IOException("Legacy scope has already been closed."));
                return false;
            }
            var stillCurrent = source.Load();
            if (source.LastState == CodexPairingState.Revoked)
            {
                target.Forget();
                _ = target.TrySealLegacyMigration();
                return false;
            }
            if (stillCurrent != candidate || source.LastState != CodexPairingState.Ready ||
                !source.ProtectedRecordExistsForMigration())
            {
                target.RecordMigrationFailure(new IOException("Legacy pairing changed during verification."));
                return false;
            }
            // Seal before writing the verified result. If writing fails, fail
            // closed instead of repeatedly resurrecting the old scope. The old
            // encrypted source remains untouched for deliberate recovery.
            if (!target.TrySealLegacyMigration()) return false;
            return target.SaveVerified(verified with { SavedAtUtc = candidate.SavedAtUtc });
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            target.RecordMigrationFailure(error);
            return false;
        }
    }

    private CodexPairingLoad ReadCanonical()
    {
        var hint = target.Load();
        return new(hint, target.LastState);
    }
    private CodexPairingLoad Failure() => new(null, target.LastState);
    private CodexPairingLoad CloseExistingScope(CodexPairingLoad current)
    {
        // A corrupt/temporarily unavailable file remains blocked without mutation.
        if (current.State is CodexPairingState.Ready or CodexPairingState.Revoked or CodexPairingState.Expired)
        {
            if (!target.TrySealLegacyMigration()) return Failure();
        }
        return current;
    }
    private IDisposable LockBoth()
    {
        // Consistent ordering across instances avoids migration/clear races and
        // deadlocks. The store mutex is recursive on the same OS thread.
        var first = string.Compare(target.RootDirectory, source.RootDirectory, StringComparison.OrdinalIgnoreCase) < 0 ? target : source;
        var second = ReferenceEquals(first, target) ? source : target;
        var firstLock = first.LockForMigration();
        try { return new TwoLocks(firstLock, second.LockForMigration()); }
        catch { firstLock.Dispose(); throw; }
    }
    private static bool IsExpectedFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException;
    private sealed class TwoLocks : IDisposable
    {
        private readonly IDisposable first;
        private readonly IDisposable second;
        internal TwoLocks(IDisposable first, IDisposable second) { this.first = first; this.second = second; }
        public void Dispose() { second.Dispose(); first.Dispose(); }
    }
}
