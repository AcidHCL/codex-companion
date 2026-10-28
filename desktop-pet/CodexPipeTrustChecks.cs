using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

// Checks never send an RPC frame or read account/conversation data.
internal static class CodexPipeTrustChecks
{
    internal static async Task<object> RunAsync(bool verifyInstalledEndpoint = false)
    {
        int assertions = 0;
        var groups = new List<string>();
        void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("Codex pipe trust check: " + message);
        }
        string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex");
        Check(CodexPipeTrust.IsAllowedInstallPath(Path.Combine(localRoot, "bin", "fixture", "Codex.exe")), "The supported official installation root should be recognized.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(localRoot + "-fake", "Codex.exe")), "A sibling directory must not match the root prefix.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(localRoot, "..", "Codex.exe")), "Path traversal must not match the allowed root.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(localRoot, "Other.exe")), "Other executable names must be rejected.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(@"\\server\OpenAI\Codex\Codex.exe"), "UNC executable paths must be rejected.");
        string storeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string package = "OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0";
        Check(CodexPipeTrust.IsAllowedInstallPath(Path.Combine(storeRoot, package, "app", "ChatGPT.exe")), "The verified Store host location must be recognized.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(storeRoot, "OpenAI.Codex_1.0_x64__impostor", "app", "ChatGPT.exe")), "Another Store publisher must be rejected.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(storeRoot, package, "tools", "ChatGPT.exe")), "The Store host must be directly in the app directory.");
        Check(!CodexPipeTrust.IsAllowedInstallPath(Path.Combine(localRoot, "ChatGPT.exe")), "The Store host filename must not be accepted in unpackaged installations.");
        groups.Add("strict_installation_path_boundaries");

        string name = "ai-pet-untrusted-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification))
        {
            Task accepting = server.WaitForConnectionAsync(timeout.Token);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await accepting.ConfigureAwait(false);
            var handle = client.SafePipeHandle;
            bool rejected = false;
            try { CodexPipeTrust.Validate(client); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected, "An unrelated same-user server must be refused before any RPC.");
            Check(handle.IsClosed, "Rejected connections must be closed.");
            int received = await server.ReadAsync(new byte[4], timeout.Token).ConfigureAwait(false);
            Check(received == 0, "The untrusted server must not receive a request or caller context.");
        }
        groups.Add("same_user_impostor_rejected_and_zero_rpc_bytes");

        // A fabricated file must fail Windows signature verification, even if a
        // future caller were to accidentally invoke the signature helper alone.
        string fixture = Path.Combine(Path.GetTempPath(), "ai-pet-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllBytes(fixture, new byte[] { 0x4d, 0x5a, 0, 0, 0, 0, 0, 0 });
            bool rejected = false;
            using (var file = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { CodexPipeTrust.ValidateAuthenticode(file, fixture); }
                catch (UnauthorizedAccessException) { rejected = true; }
            }
            Check(rejected, "An unsigned executable must not be accepted as an official signer.");
        }
        finally { File.Delete(fixture); }
        groups.Add("unsigned_authenticode_rejected");

        bool installedEndpointVerified = false;
        if (verifyInstalledEndpoint)
        {
            if (!CodexEndpointPolicy.TryNormalize(Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH"), out var endpoint))
                throw new InvalidOperationException("当前环境未提供可验证的 Codex 本机接口；测试不会猜测地址。");
            using var pipe = new NamedPipeClientStream(".", endpoint.Substring(CodexEndpointPolicy.PipePathPrefix.Length), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            CodexPipeTrust.Validate(pipe);
            Check(pipe.IsConnected, "The real installed Codex endpoint must pass identity validation.");
            installedEndpointVerified = true;
            groups.Add("installed_codex_process_owner_session_path_and_verified_publisher");
        }
        return new { passed = true, assertions, groups, installedEndpointVerified, rpcRequestsSent = 0, offlineSignatureVerification = true };
    }
}
