using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal enum CodexPairingState { Unknown, Ready, Missing, Expired, Revoked, Unavailable, Invalid }

internal interface ICodexConnection : IDisposable
{
    string ConnectionDescription { get; }
    string? CallerThreadId { get; }
    bool IsPairingRevoked { get; }
    // Cached diagnostic state in production; legacy synthetic transports need no changes.
    CodexPairingState PairingState => IsPairingRevoked ? CodexPairingState.Revoked : CodexPairingState.Ready;
    bool HasSavedPairing { get; }
    bool ResumeSavedPairing();
    void SetMessageAccess(bool enabled);
    Task EnsureConnectedAsync(CancellationToken token = default);
    Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default);
    void Disconnect();
    Task ForgetAsync();
}
