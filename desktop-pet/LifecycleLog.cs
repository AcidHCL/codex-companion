using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace DesktopPet;

// This schema deliberately has no caller-supplied text fields. In particular,
// neither exception messages/type names nor paths/arguments can enter the log.
internal enum LifecycleEvent
{
    Startup, ExitPetMenu, ExitTrayMenu, ExitCommand, SessionLogoff,
    SessionShutdown, ApplicationExit, ProcessExit, StartupFailure,
    DispatcherUnhandledException, DomainUnhandledException,
    GraphicsRecoveryStarted, GraphicsRecoveryChildStarted,
    GraphicsRecoveryUnavailable, GraphicsRecoveryFailed, GraphicsRecoveryExit,
    ConnectionRetry, ConnectionRecovered, ConnectionEstablished, ChatStorageFailure
}

internal enum LifecycleFailureKind
{
    None, Graphics, Cancelled, Timeout, AccessDenied, Security,
    Io, OutOfMemory, Com, Win32, InvalidOperation, Other
}

internal readonly record struct LifecycleFailure(LifecycleFailureKind Kind, int HResult);
internal readonly record struct LifecycleContext(int ProcessId, int SessionId, int? ParentProcessId,
    int VersionMajor, int VersionMinor, int VersionBuild, int VersionRevision,
    bool RecoveryLaunch, bool CompatibilityMode);

internal static class LifecycleLog
{
    private static LifecycleLogSink? _sink;

    // Only Program.Main's normal singleton instance calls this, after every
    // diagnostic/test/--exit helper has returned. Logging is otherwise a no-op.
    internal static void Start()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            Version version = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0);
            var context = new LifecycleContext(Environment.ProcessId, process.SessionId, ParentProcessId(),
                version.Major, version.Minor, version.Build, version.Revision,
                GraphicsRecovery.IsRecoveryLaunch, GraphicsRecovery.CompatibilityMode);
            _sink = new LifecycleLogSink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPet"), context);
            Record(LifecycleEvent.Startup);
        }
        catch { /* Diagnostics must never prevent the pet starting. */ }
    }

    internal static void Record(LifecycleEvent kind, int? code = null)
    {
        try { _sink?.Write(kind, default, code); } catch { }
    }

    internal static void RecordException(LifecycleEvent kind, Exception exception)
    {
        try { _sink?.Write(kind, Classify(exception)); } catch { }
    }

    internal static LifecycleFailure Classify(Exception exception) => new(
        GraphicsFailure.IsFatal(exception) ? LifecycleFailureKind.Graphics : exception switch
        {
            OperationCanceledException => LifecycleFailureKind.Cancelled,
            TimeoutException => LifecycleFailureKind.Timeout,
            UnauthorizedAccessException => LifecycleFailureKind.AccessDenied,
            SecurityException => LifecycleFailureKind.Security,
            IOException => LifecycleFailureKind.Io,
            OutOfMemoryException => LifecycleFailureKind.OutOfMemory,
            COMException => LifecycleFailureKind.Com,
            Win32Exception => LifecycleFailureKind.Win32,
            InvalidOperationException => LifecycleFailureKind.InvalidOperation,
            _ => LifecycleFailureKind.Other
        }, exception.HResult);

    private static int? ParentProcessId()
    {
        try
        {
            // Only the current process's fixed numeric basic information. No
            // process enumeration, command lines, paths or other-user details.
            if (NtQueryInformationProcess(GetCurrentProcess(), 0, out var info,
                Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            long parent = info.ParentProcessId.ToInt64();
            return parent is > 0 and <= int.MaxValue ? (int)parent : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal IntPtr Reserved1;
        internal IntPtr PebBaseAddress;
        internal IntPtr Reserved2;
        internal IntPtr Reserved3;
        internal IntPtr ProcessId;
        internal IntPtr ParentProcessId;
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        out ProcessBasicInformation information, int informationLength, out int returnedLength);
}

internal sealed class LifecycleLogSink
{
    internal const int MaximumFileBytes = 128 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly LifecycleContext _context;
    private readonly int _maximumBytes;
    private long _sequence;

    internal LifecycleLogSink(string directory, LifecycleContext context, int maximumBytes = MaximumFileBytes)
    {
        if (maximumBytes < 1024 || maximumBytes > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _directory = directory;
        _context = context;
        _maximumBytes = maximumBytes;
    }

    internal bool Write(LifecycleEvent kind, LifecycleFailure failure = default, int? code = null)
    {
        // Undefined enum values must not accidentally become arbitrary labels.
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(failure.Kind)) return false;
        lock (_gate)
        {
            var entry = new
            {
                schema = 1, utc = DateTimeOffset.UtcNow, sequence = ++_sequence,
                eventName = kind.ToString(), pid = _context.ProcessId,
                session = _context.SessionId, parentPid = _context.ParentProcessId,
                version = new[] { _context.VersionMajor, _context.VersionMinor, _context.VersionBuild, _context.VersionRevision },
                recovery = _context.RecoveryLaunch, compatibility = _context.CompatibilityMode,
                failure = failure.Kind.ToString(), hresult = failure.HResult, code
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry);
            if (json.Length + 1 > _maximumBytes) return false;
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "lifecycle.jsonl");
            string previous = Path.Combine(_directory, "lifecycle.previous.jsonl");
            if (File.Exists(previous) && new FileInfo(previous).Length > _maximumBytes) File.Delete(previous);
            if (File.Exists(path) && new FileInfo(path).Length + json.Length + 1 > _maximumBytes)
            {
                if (new FileInfo(path).Length > _maximumBytes) File.Delete(path);
                else File.Move(path, previous, overwrite: true);
            }
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(json);
            stream.WriteByte((byte)'\n');
            return true;
        }
    }
}
