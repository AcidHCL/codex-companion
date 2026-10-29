using System.Linq;

namespace DesktopPet;

internal static class StartupModePolicy
{
    // These commands can touch the real pairing store, even when their output is
    // only a diagnostic report. A test-looking suffix is not proof of isolation.
    internal static bool UsesRealConnection(string[] args) => args.Any(value => value is
        "--pair-codex" or "--connection-test" or "--desktop-environment-test" or "--integration-test");

    // Keep this explicit list aligned with Main's returning test branches and
    // synthetic smoke modes. Unknown arguments may otherwise reach the real app.
    internal static bool IsIsolatedDiagnostic(string[] args) => !UsesRealConnection(args) && args.Any(value => value is
        "--self-test" or "--speech-stream-live-test" or "--voice-worker-lifecycle-test" or "--speech-pcm-live-test" or "--streaming-text-test" or "--voice-input-test" or "--voice-input-ui-test" or "--voice-input-live-test" or "--microphone-test" or "--monitor-recovery-test" or "--lifecycle-log-test" or
        "--pairing-recovery-test" or "--pairing-migration-test" or "--version-ui-test" or
        "--topmost-test" or "--display-recovery-test" or "--remote-session-test" or
        "--task-source-test" or "--privacy-ui-test" or "--scene-audio-ui-test" or "--speech-test" or "--speech-ui-test" or "--speech-live-test" or "--security-test" or "--pipe-trust-test" or
        "--ollama-client-test" or "--ollama-chat-test" or "--ollama-live-test" or "--pet-companion-ui-test" or
        "--chat-store-test" or "--shared-memory-test" or "--chat-history-ui-test" or "--chat-memory-live-test" or
        "--pet-click-interaction-test" or "--pet-click-pointer-fixture" or "--pet-behavior-planner-test" or "--pet-behavior-queue-test" or "--pet-behavior-motion-test" or "--pet-behavior-chat-test" or
        "--pet-behavior-live-test" or "--pet-behavior-coordinator-test" or "--deepseek-motion-test" or "--redrawn-motion-test" or "--redrawn-animation-test" or
        "--web-client-test" or "--web-chat-test" or "--web-ui-test" or "--web-live-test" or
        "--smoke-test" or "--live2d-test" or "--scene-test" or "--movement-test" or
        "--deepseek-state-test" or "--fall-land-test" or "--drag-state-test" or
        "--movement-fixture" or "--graphics-recovery-parent-probe" or "--graphics-recovery-child-probe");
}
