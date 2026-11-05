using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal sealed record OllamaGpuProcess(int ProcessId, string Name, double UtilizationPercent);
internal sealed record OllamaGpuActivity(bool Available, string? UnavailableReason,
    double? UtilizationPercent, IReadOnlyList<OllamaGpuProcess> Processes, DateTimeOffset SampledAt)
{
    public int? ServerProcessId { get; init; }
}

// This reader observes Windows counters only. It never opens an Ollama request,
// reads a prompt/command line, starts a helper process, or initializes a GPU.
internal static class OllamaActivityTelemetry
{
    private static readonly SemaphoreSlim QueryGate = new(1, 1);
    private const uint PdhMoreData = 0x800007D2, PdhDouble = 0x200;
    private const int MaxNativeBuffer = 16 * 1024 * 1024;
    internal sealed record ProcessEntry(int ProcessId, int ParentProcessId, string Name);
    internal sealed record EngineValue(int ProcessId, string Engine, double Percent);
    private sealed record ProcessIdentity(int ProcessId, string Path, ulong Created);

    internal static async Task<OllamaGpuActivity> ReadAsync(string endpoint, CancellationToken cancellationToken)
    {
        Uri address;
        try { address = OllamaEndpoint.Parse(endpoint); }
        catch (ArgumentException) { return Unavailable(L.T("服务器地址无效。", "The server address is invalid.")); }
        if (!address.IsLoopback)
            return Unavailable(L.T("远程 Ollama 的进程负载无法从本机读取。", "This computer cannot read a remote Ollama server's process activity."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        CancellationToken readToken = deadline.Token;
        try
        {
            // Native snapshots and counter collection must not block the WPF thread.
            return await Task.Run(async () =>
            {
                await QueryGate.WaitAsync(readToken).ConfigureAwait(false);
                try { return await ReadNativeAsync(address, readToken).ConfigureAwait(false); }
                finally { QueryGate.Release(); }
            }, readToken).WaitAsync(readToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Unavailable(L.T("进程负载查询超时，请稍后刷新。", "The process activity query timed out. Refresh again shortly.")); }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException or InvalidDataException)
        { return Unavailable(L.T("Windows 暂时无法提供 Ollama 进程负载。", "Windows cannot currently provide Ollama process activity.")); }
    }

    private static async Task<OllamaGpuActivity> ReadNativeAsync(Uri endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int? server = FindServer(endpoint);
        if (server == null)
            return Unavailable(L.T("无法唯一确认此地址的本机 Ollama 服务进程。", "The local Ollama service process for this address could not be uniquely identified."));
        var processes = SnapshotProcesses();
        var serverEntry = processes.FirstOrDefault(p => p.ProcessId == server);
        var serverIdentity = ReadIdentity(server.Value);
        if (serverEntry == null || !serverEntry.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase) || serverIdentity == null)
            return Unavailable(L.T("此端口不是可确认的本机 Ollama 服务，或没有读取权限。", "This port is not a verified local Ollama service, or process access is unavailable."));
        string directory = Path.GetDirectoryName(serverIdentity.Path) ?? "";
        var observed = SelectDescendants(processes, server.Value)
            .Select(p => (Entry: p, Identity: ReadIdentity(p.ProcessId))).ToArray();
        if (observed.Any(p => p.Identity == null || p.Identity.Created < serverIdentity.Created || !IsWithinInstall(p.Identity.Path, directory)))
            return Unavailable(L.T("部分 Ollama 进程的身份无法核验，暂不估计负载。", "Some Ollama process identities could not be verified; activity is unknown."), server);
        var candidates = observed;
        if (candidates.Length == 0)
            return Unavailable(L.T("暂时无法读取 Ollama 推理进程。", "Ollama inference processes cannot currently be read."), server);

        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, UIntPtr.Zero, out query) != 0 || query == IntPtr.Zero ||
                PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out IntPtr counter) != 0)
                return CounterUnavailable(server);
            if (PdhCollectQueryData(query) != 0) return CounterUnavailable(server);
            // Rate counters need two observations. A missing/invalid rate is never 0%.
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (PdhCollectQueryData(query) != 0) return CounterUnavailable(server);
            ct.ThrowIfCancellationRequested();
            var engines = ReadCounterValues(counter);
            if (FindServer(endpoint) != server || ReadIdentity(server.Value) != serverIdentity)
                return Unavailable(L.T("Ollama 服务已变化，等待下一次采样。", "The Ollama service changed. Waiting for the next sample."));
            var currentCandidates = SelectDescendants(SnapshotProcesses(), server.Value).Select(p => p.ProcessId).OrderBy(p => p).ToArray();
            if (!currentCandidates.SequenceEqual(candidates.Select(p => p.Entry.ProcessId).OrderBy(p => p)) ||
                candidates.Any(p => ReadIdentity(p.Entry.ProcessId) != p.Identity))
                return Unavailable(L.T("Ollama 推理进程已变化，等待下一次采样。", "Ollama inference processes changed. Waiting for the next sample."), server);
            var stable = candidates.Select(p => p.Entry).ToArray();
            return Aggregate(stable, engines, server.Value, DateTimeOffset.UtcNow);
        }
        finally { if (query != IntPtr.Zero) PdhCloseQuery(query); }
    }

    internal static IReadOnlyList<ProcessEntry> SelectDescendants(IReadOnlyList<ProcessEntry> all, int server)
    {
        var byId = all.GroupBy(p => p.ProcessId).ToDictionary(p => p.Key, p => p.First());
        return all.Where(p => p.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)).Where(p =>
        {
            int ancestor = p.ProcessId;
            var seen = new HashSet<int>();
            for (int depth = 0; depth < 32 && seen.Add(ancestor); depth++)
            {
                if (ancestor == server) return true;
                if (!byId.TryGetValue(ancestor, out var entry)) return false;
                ancestor = entry.ParentProcessId;
            }
            return false;
        }).ToArray();
    }

    internal static bool IsWithinInstall(string candidate, string directory) => directory.Length > 0 &&
        candidate.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static OllamaGpuActivity Aggregate(IReadOnlyList<ProcessEntry> processes,
        IReadOnlyList<EngineValue> engines, int server, DateTimeOffset sampledAt)
    {
        var names = processes.ToDictionary(p => p.ProcessId, p => p.Name);
        var known = engines.Where(e => names.ContainsKey(e.ProcessId) && double.IsFinite(e.Percent) && e.Percent >= 0)
            .GroupBy(e => (e.ProcessId, e.Engine)).Select(g => g.OrderByDescending(e => e.Percent).First()).ToArray();
        if (known.Length == 0) return CounterUnavailable(server) with { SampledAt = sampledAt };
        // The service host may have no GPU counters, but a runner without a valid
        // counter must not disappear into a misleading 0% service estimate.
        var required = processes.Where(p => p.ProcessId != server).ToArray();
        if (required.Length == 0) required = processes.ToArray();
        if (required.Any(p => !known.Any(e => e.ProcessId == p.ProcessId)))
            return CounterUnavailable(server) with { SampledAt = sampledAt };
        // Match Task Manager's busiest-engine convention. Sum different Ollama
        // processes sharing an engine, but do not sum unrelated/overlapping engines.
        double total = known.GroupBy(e => e.Engine).Max(g => Math.Min(100, g.Sum(e => e.Percent)));
        var rows = known.GroupBy(e => e.ProcessId).Select(g =>
            new OllamaGpuProcess(g.Key, names[g.Key], Math.Min(100, g.Max(e => e.Percent))))
            .OrderByDescending(p => p.UtilizationPercent).ThenBy(p => p.ProcessId).ToArray();
        return new(true, null, total, rows, sampledAt) { ServerProcessId = server };
    }

    internal static bool TryEngine(string name, double percent, uint status, out EngineValue? value)
    {
        value = null;
        if (status > 1 || !double.IsFinite(percent) || percent < 0 || !name.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int separator = name.IndexOf('_', 4);
        if (separator < 5 || !int.TryParse(name.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0)
            return false;
        string engine = name[(separator + 1)..];
        if (!engine.StartsWith("luid_", StringComparison.Ordinal) || !engine.Contains("_eng_", StringComparison.Ordinal)) return false;
        value = new(processId, engine, Math.Min(100, percent)); return true;
    }

    private static IReadOnlyList<EngineValue> ReadCounterValues(IntPtr counter)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            uint result = PdhGetFormattedCounterArray(counter, PdhDouble, ref bytes, out _, IntPtr.Zero);
            if (result != PdhMoreData || bytes == 0 || bytes > MaxNativeBuffer) return Array.Empty<EngineValue>();
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint allocated = bytes;
                result = PdhGetFormattedCounterArray(counter, PdhDouble, ref bytes, out uint count, buffer);
                if (result == PdhMoreData) continue;
                int itemSize = Marshal.SizeOf<FormattedItem>();
                if (result != 0 || count > allocated / itemSize) return Array.Empty<EngineValue>();
                var rows = new List<EngineValue>();
                for (int index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<FormattedItem>(IntPtr.Add(buffer, index * itemSize));
                    // Names are pointers into the native result allocation.
                    long offset = item.Name.ToInt64() - buffer.ToInt64();
                    if (offset < 0 || offset >= allocated) continue;
                    int maximumCharacters = Math.Min(2048, (int)((allocated - offset) / 2));
                    int length = 0;
                    while (length < maximumCharacters && Marshal.ReadInt16(item.Name, length * 2) != 0) length++;
                    string? name = length < maximumCharacters ? Marshal.PtrToStringUni(item.Name, length) : null;
                    if (name != null && TryEngine(name, item.Value.Number, item.Value.Status, out var row)) rows.Add(row!);
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return Array.Empty<EngineValue>();
    }

    private static int? FindServer(Uri endpoint)
    {
        string host = endpoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } :
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : Array.Empty<IPAddress>();
        var owners = new HashSet<int>();
        foreach (var address in addresses)
            foreach (int owner in TcpListeners(address, endpoint.Port)) owners.Add(owner);
        return owners.Count == 1 ? owners.Single() : null;
    }

    private static IReadOnlyList<int> TcpListeners(IPAddress address, int port)
    {
        bool ipv6 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        uint family = ipv6 ? 23u : 2u;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bytes = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref bytes, false, family, 3, 0);
            if (result != 122 || bytes < 4 || bytes > MaxNativeBuffer) throw new Win32Exception((int)result);
            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                uint allocated = bytes;
                result = GetExtendedTcpTable(buffer, ref bytes, false, family, 3, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                int rowSize = ipv6 ? 56 : 24;
                if (count > (allocated - 4) / rowSize) throw new InvalidDataException();
                var owners = new List<int>();
                for (int index = 0; index < count; index++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + index * rowSize);
                    int localPortOffset = ipv6 ? 20 : 8;
                    int localPort = (Marshal.ReadByte(row, localPortOffset) << 8) | Marshal.ReadByte(row, localPortOffset + 1);
                    if (localPort != port) continue;
                    byte[] localBytes = new byte[ipv6 ? 16 : 4];
                    Marshal.Copy(IntPtr.Add(row, ipv6 ? 0 : 4), localBytes, 0, localBytes.Length);
                    var local = new IPAddress(localBytes);
                    if (!local.Equals(address) && !local.Equals(ipv6 ? IPAddress.IPv6Any : IPAddress.Any)) continue;
                    int owner = Marshal.ReadInt32(row, ipv6 ? 52 : 20);
                    if (owner > 0) owners.Add(owner);
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidDataException();
    }

    private static IReadOnlyList<ProcessEntry> SnapshotProcesses()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new NativeProcessEntry { Size = (uint)Marshal.SizeOf<NativeProcessEntry>(), Name = "" };
            var rows = new List<ProcessEntry>();
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { rows.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.Name)); }
            while (Process32Next(snapshot, ref entry));
            return rows;
        }
        finally { CloseHandle(snapshot); }
    }

    private static ProcessIdentity? ReadIdentity(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new System.Text.StringBuilder(32768); uint size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size) ||
                !GetProcessTimes(handle, out long created, out _, out _, out _)) return null;
            return new(processId, path.ToString(), unchecked((ulong)created));
        }
        finally { CloseHandle(handle); }
    }

    private static OllamaGpuActivity CounterUnavailable(int? server) => Unavailable(L.T(
        "未取得有效的 Ollama GPU 计数器；不能把缺少数据当作空闲。",
        "Valid Ollama GPU counters are unavailable; missing data does not mean idle."), server);
    private static OllamaGpuActivity Unavailable(string reason, int? server = null) =>
        new(false, reason, null, Array.Empty<OllamaGpuProcess>(), DateTimeOffset.UtcNow) { ServerProcessId = server };

    [StructLayout(LayoutKind.Sequential)] private struct FormattedValue { public uint Status; public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct FormattedItem { public IntPtr Name; public FormattedValue Value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeProcessEntry
    {
        public uint Size, Usage, ProcessId; public UIntPtr HeapId; public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    // Native layouts/API contracts: learn.microsoft.com/windows/win32/api/pdh/
    // nf-pdh-pdhgetformattedcounterarrayw and iphlpapi/nf-iphlpapi-getextendedtcptable.
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, UIntPtr user, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sorted, uint family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref NativeProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref NativeProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
