using System;
using System.Runtime.InteropServices;

namespace DesktopPet;

// Unknown fields stay nullable: an unavailable WTS query must never turn a
// known locked/disconnected session into an active one.
internal readonly record struct WindowsSessionSnapshot(
    int SessionId, bool? IsRemote, bool? IsConnected, bool? IsLocked)
{
    internal bool IsRemoteActive => IsRemote == true && IsConnected == true && IsLocked == false;
}

internal static class WindowsSessionState
{
    internal static int CurrentSessionId => ProcessIdToSessionId((uint)Environment.ProcessId, out uint id)
        ? unchecked((int)id) : -1;

    internal static WindowsSessionSnapshot QueryCurrent()
    {
        int sessionId = CurrentSessionId;
        bool? remote = null, connected = null, locked = null;
        if (sessionId < 0) return new(sessionId, remote, connected, locked);

        // These calls query only this process's session, never enumerate other
        // users or read client addresses, user names, or credentials.
        if (TryQuery(16, out var buffer, out int bytes)) // WTSClientProtocolType: USHORT
        {
            try
            {
                if (bytes >= sizeof(ushort))
                {
                    int protocol = (ushort)Marshal.ReadInt16(buffer);
                    if (protocol is 0 or 2) remote = protocol == 2;
                }
            }
            finally { WTSFreeMemory(buffer); }
        }
        // A true metric is useful when WTS is temporarily unavailable. A false
        // metric is not proof of a console session (RemoteFX can report false).
        if (!remote.HasValue && GetSystemMetrics(0x1000) != 0) remote = true;

        if (TryQuery(8, out buffer, out bytes)) // WTSConnectState
        {
            try
            {
                if (bytes >= sizeof(int)) connected = ConnectedState(Marshal.ReadInt32(buffer));
            }
            finally { WTSFreeMemory(buffer); }
        }
        if (TryQuery(25, out buffer, out bytes)) // WTSSessionInfoEx
        {
            try
            {
                // WTSINFOEXW's Level1 union is aligned to 8 bytes because it
                // contains LARGE_INTEGER fields. Read only its public prefix.
                if (bytes >= 20 && Marshal.ReadInt32(buffer, 0) == 1 &&
                    Marshal.ReadInt32(buffer, 8) == sessionId)
                {
                    connected = ConnectedState(Marshal.ReadInt32(buffer, 12)) ?? connected;
                    locked = Marshal.ReadInt32(buffer, 16) switch { 0 => true, 1 => false, _ => null };
                }
            }
            finally { WTSFreeMemory(buffer); }
        }
        return new(sessionId, remote, connected, locked);
    }

    private static bool? ConnectedState(int state) => state is >= 0 and <= 9 ? state == 0 : null;

    private static bool TryQuery(int infoClass, out IntPtr buffer, out int bytes)
    {
        bool success = WTSQuerySessionInformationW(IntPtr.Zero, uint.MaxValue, infoClass, out buffer, out bytes);
        if (success && buffer != IntPtr.Zero) return true;
        if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        buffer = IntPtr.Zero; bytes = 0;
        return false;
    }

    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
