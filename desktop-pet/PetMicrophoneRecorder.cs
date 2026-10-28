using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal interface IPetMicrophoneRecorder : IDisposable
{
    bool IsRecording { get; }
    event Action? LimitReached;
    void Start();
    byte[] Stop();
    void Cancel();
}

// WinMM signals an event rather than invoking managed callbacks. One worker
// owns every native handle/buffer and makes all waveIn calls, including reset.
internal sealed class PetMicrophoneRecorder : IPetMicrophoneRecorder
{
    internal const int SampleRate = 16000;
    internal const int MaximumSeconds = 60;
    internal const int MaximumPcmBytes = SampleRate * 2 * MaximumSeconds;
    private const int BufferBytes = 3200;
    private static readonly byte[] ZeroBuffer = new byte[BufferBytes];
    private const uint CallbackEvent = 0x00050000, WaveMapper = uint.MaxValue;
    private const uint HeaderDone = 0x00000001;
    private const string CleanupError = "麦克风未能正常释放，请先完全退出桌宠，重新连接输入设备后再启动。";
    private static readonly object FailedDeviceGate = new();
    private static readonly List<AutoResetEvent> FailedDeviceEvents = new();
    private static int _nativeCleanupFaulted;
    private readonly object _operation = new();
    private Session? _session;
    private bool _disposed;
    public event Action? LimitReached;

    public bool IsRecording
    {
        get { lock (_operation) return _session is { Started: true } session && !session.StopRequested && !session.Finished.Task.IsCompleted; }
    }

    // Read-only device enumeration; this does not open the microphone.
    internal static uint GetAvailableDeviceCount() => waveInGetNumDevs();

    public void Start()
    {
        lock (_operation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _nativeCleanupFaulted) != 0) throw new InvalidOperationException(CleanupError);
            if (_session != null) throw new InvalidOperationException("请先结束上一段录音，再开始新的语音输入。");
            if (GetAvailableDeviceCount() == 0) throw new InvalidOperationException("未检测到麦克风，请连接输入设备并检查 Windows 声音设置。");
            var session = new Session();
            _session = session;
            var worker = new Thread(() => Capture(session)) { IsBackground = true, Name = "AI Pet microphone capture" };
            try { worker.Start(); }
            catch { _session = null; session.Audio.Clear(); session.Wake.Dispose(); throw; }
            if (!session.Ready.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                session.RequestStop();
                throw new InvalidOperationException("麦克风启动超时，请检查输入设备后重试。");
            }
            if (session.Error != null)
            {
                if (session.Finished.Task.IsCompleted) { session.Audio.Clear(); _session = null; }
                throw new InvalidOperationException(session.Error);
            }
        }
    }

    public byte[] Stop()
    {
        lock (_operation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = _session;
            if (session == null) return PcmAudioBuffer.BuildWave(ReadOnlySpan<byte>.Empty);
            session.RequestStop();
            if (!session.Finished.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("麦克风尚未停止，请检查输入设备后重试。");
            _session = null;
            try
            {
                if (session.Error != null) throw new InvalidOperationException(session.Error);
                return session.Audio.ToWave();
            }
            finally { session.Audio.Clear(); }
        }
    }

    public void Cancel()
    {
        lock (_operation) CancelCore();
    }

    private void CancelCore()
    {
        var session = _session;
        if (session == null) return;
        session.Discard = true;
        session.RequestStop();
        // A faulty device driver can hang inside native code. The worker keeps
        // owning its buffers until it returns; never free memory still in use.
        if (session.Finished.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            session.Audio.Clear();
            _session = null;
        }
    }

    private void Capture(Session session)
    {
        IntPtr input = IntPtr.Zero;
        var buffers = new NativeBuffer[8];
        bool reachedLimit = false;
        bool inputClosed = false, cleanupFailed = false;
        int next = 0;
        try
        {
            if (Volatile.Read(ref _nativeCleanupFaulted) != 0) throw new InvalidOperationException(CleanupError);
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0
            };
            Check(waveInOpen(out input, WaveMapper, ref format, session.Wake.SafeWaitHandle.DangerousGetHandle(), UIntPtr.Zero, CallbackEvent));
            for (int i = 0; i < buffers.Length; i++)
            {
                var buffer = new NativeBuffer(BufferBytes);
                buffers[i] = buffer;
                Check(waveInPrepareHeader(input, buffer.Header, HeaderSize)); buffer.Prepared = true;
                Check(waveInAddBuffer(input, buffer.Header, HeaderSize));
            }
            if (session.StopRequested) return;
            Check(waveInStart(input));
            session.Started = true;
            session.Ready.TrySetResult();
            var elapsed = Stopwatch.StartNew();
            var copy = new byte[BufferBytes];
            while (!session.StopRequested)
            {
                session.Wake.WaitOne(100);
                if (session.StopRequested) break;
                for (int count = 0; count < buffers.Length; count++)
                {
                    NativeBuffer buffer = buffers[next];
                    var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                    if ((header.Flags & HeaderDone) == 0) break;
                    AppendBuffer(session, header, copy);
                    if (session.Audio.Length >= MaximumPcmBytes)
                    {
                        reachedLimit = true;
                        session.RequestStop();
                        break;
                    }
                    // Requeue the same prepared header. WinMM clears DONE and
                    // sets INQUEUE itself; do not overwrite its private fields.
                    Check(waveInAddBuffer(input, buffer.Header, HeaderSize));
                    next = (next + 1) % buffers.Length;
                }
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(MaximumSeconds))
                {
                    reachedLimit = true;
                    session.RequestStop();
                }
            }
        }
        catch (Exception ex)
        {
            session.Error = ex is InvalidOperationException ? ex.Message : "麦克风录音中断，请检查输入设备后重试。";
        }
        finally
        {
            session.Started = false;
            if (input != IntPtr.Zero)
            {
                // Reset stops capture and returns every queued buffer, including
                // the partially filled tail, before any memory can be released.
                uint reset = waveInReset(input);
                if (reset != 0) cleanupFailed = true;
                if (reset == 0 && !session.Discard && session.Error == null)
                {
                    var copy = new byte[BufferBytes];
                    for (int count = 0; count < buffers.Length; count++)
                    {
                        var buffer = buffers[(next + count) % buffers.Length];
                        if (buffer == null) continue;
                        var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                        if ((header.Flags & HeaderDone) != 0) AppendBuffer(session, header, copy);
                    }
                }
                foreach (var buffer in buffers)
                {
                    if (buffer == null) continue;
                    if (buffer.Prepared)
                    {
                        uint unprepare = waveInUnprepareHeader(input, buffer.Header, HeaderSize);
                        if (unprepare == 0) buffer.Prepared = false;
                        else cleanupFailed = true;
                    }
                    if (!buffer.Prepared) buffer.Free();
                    // On a broken driver leave any still-owned native block
                    // allocated rather than risking a driver use-after-free.
                }
                uint close = waveInClose(input);
                inputClosed = close == 0;
                if (!inputClosed) cleanupFailed = true;
            }
            else { inputClosed = true; foreach (var buffer in buffers) buffer?.Free(); }
            if (cleanupFailed)
            {
                session.Error = CleanupError;
                // Do not allow repeated reopen attempts to accumulate quarantined
                // driver resources. Only already-running captures may finish now.
                Interlocked.Exchange(ref _nativeCleanupFaulted, 1);
            }
            if (inputClosed) session.Wake.Dispose();
            else
            {
                // A handle whose close failed may still signal this event. Retain
                // it until process exit; GC/disposal would permit handle reuse.
                // The process-wide fault prevents any subsequent capture starts.
                lock (FailedDeviceGate) FailedDeviceEvents.Add(session.Wake);
            }
            if (session.Discard) session.Audio.Clear();
            session.Finished.TrySetResult();
            session.Ready.TrySetResult();
            bool notify;
            lock (_operation) notify = reachedLimit && !_disposed && !session.Discard && ReferenceEquals(_session, session);
            if (notify)
            {
                try { LimitReached?.Invoke(); }
                catch (Exception) { /* UI consumer owns event error handling. */ }
            }
        }
    }

    private static void AppendBuffer(Session session, WaveHeader header, byte[] copy)
    {
        int length = (int)Math.Min(header.BytesRecorded, (uint)copy.Length);
        length &= ~1;
        if (session.Discard || length == 0) return;
        Marshal.Copy(header.Data, copy, 0, length);
        session.Audio.Append(copy.AsSpan(0, length));
        Array.Clear(copy, 0, length);
    }

    private static void Check(uint result)
    { if (result != 0) throw new InvalidOperationException(ErrorMessage(result)); }

    private static string ErrorMessage(uint result) => result switch
    {
        2 or 6 => "未找到可用的麦克风，请检查 Windows 默认输入设备。",
        4 => "麦克风被其他程序独占，请释放设备后重试。",
        7 => "无法分配录音缓冲区，请稍后重试。",
        32 => "当前麦克风不支持所需的录音格式，请更换 Windows 默认输入设备。",
        _ => "无法使用麦克风，请检查 Windows 麦克风权限、默认输入设备及占用情况。"
    };

    public void Dispose()
    {
        lock (_operation)
        {
            if (_disposed) return;
            _disposed = true; LimitReached = null;
            CancelCore();
        }
    }

    private sealed class Session
    {
        internal readonly AutoResetEvent Wake = new(false);
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly PcmAudioBuffer Audio = new();
        internal volatile bool Started, Discard;
        internal string? Error;
        private int _stop;
        internal bool StopRequested => Volatile.Read(ref _stop) != 0;
        internal void RequestStop()
        {
            Interlocked.Exchange(ref _stop, 1);
            try { Wake.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed class NativeBuffer
    {
        internal IntPtr Header, Data;
        internal bool Prepared;
        private readonly int _length;
        internal NativeBuffer(int length)
        {
            _length = length;
            Data = Marshal.AllocHGlobal(length);
            try
            {
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)length }, Header, false);
            }
            catch { if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); throw; }
        }
        internal void Free()
        {
            if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); Header = IntPtr.Zero; }
            if (Data != IntPtr.Zero)
            {
                Marshal.Copy(ZeroBuffer, 0, Data, _length);
                Marshal.FreeHGlobal(Data); Data = IntPtr.Zero;
            }
        }
    }

    internal sealed class PcmAudioBuffer
    {
        private readonly MemoryStream _pcm = new(MaximumPcmBytes);
        internal int Length => (int)_pcm.Length;
        internal void Append(ReadOnlySpan<byte> bytes)
        {
            int count = Math.Min(MaximumPcmBytes - Length, bytes.Length) & ~1;
            if (count > 0) _pcm.Write(bytes[..count]);
        }
        internal byte[] ToWave() => BuildWave(_pcm.GetBuffer().AsSpan(0, Length));
        internal void Clear()
        {
            Array.Clear(_pcm.GetBuffer(), 0, Length);
            _pcm.SetLength(0); _pcm.Position = 0;
        }
        internal static byte[] BuildWave(ReadOnlySpan<byte> pcm)
        {
            if (pcm.Length > MaximumPcmBytes || pcm.Length % 2 != 0) throw new ArgumentException("PCM input must be aligned and no longer than 60 seconds.", nameof(pcm));
            byte[] wave = new byte[44 + pcm.Length];
            "RIFF"u8.CopyTo(wave.AsSpan(0, 4));
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), 36 + pcm.Length);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8, 8));
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), SampleRate * 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
            "data"u8.CopyTo(wave.AsSpan(36, 4));
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), pcm.Length);
            pcm.CopyTo(wave.AsSpan(44));
            return wave;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        internal ushort FormatTag, Channels;
        internal uint SamplesPerSecond, AverageBytesPerSecond;
        internal ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength, BytesRecorded;
        internal UIntPtr User;
        internal uint Flags, Loops;
        internal IntPtr Next;
        internal UIntPtr Reserved;
    }

    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll", ExactSpelling = true)] private static extern uint waveInClose(IntPtr handle);
}
