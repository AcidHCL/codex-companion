using System.Windows;

namespace DesktopPet;

// Remote Desktop can turn off client-area animation as a transport preference.
// That flag must not disable pet scenes and gravity in a connected, unlocked
// remote session. Local reduced-motion behavior remains unchanged; visibility,
// display availability and the user's independent motion/movement switches are
// still applied by the host.
internal readonly record struct MotionPreferences(bool SystemAnimationsEnabled, bool IsActiveRemoteSession)
{
    public bool AllowsAnimation => SystemAnimationsEnabled || IsActiveRemoteSession;

    public static MotionPreferences Capture(WindowsSessionSnapshot session) =>
        new(SystemParameters.ClientAreaAnimation, session.IsRemoteActive);
}
