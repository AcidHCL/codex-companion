using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DesktopPet;

// One worker owns waveOut and every native buffer. The producer never waits for
// a chunk to finish playing: it fills a bounded queue while waveOut drains it.
internal sealed class PetPcmStreamPlayer : IPetPcmStreamPlayer
{
    private readonly object _guard = new();
    private readonly SemaphoreSlim _playback = new(1, 1);
    private Session? _session;
    private double _volume = .6;
    private bool _disposed;
    private static readonly List<Session> FailedDevices = new();
    private static int _cleanupFailed;
    private const int NativeSlots = 24;
    private const uint HeaderDone = 1, CallbackEvent = 0x50000;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();

    public async Task PlayAsync(IAsyncEnumerable<PetPcmChunk> source, double volume,
        Action<bool> playbackState, CancellationToken cancellationToken)
    {
        await _playback.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? session = null;
        try
        {
            lock (_guard)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (Volatile.Read(ref _cleanupFailed) != 0) throw new InvalidOperationException("声音设备未能释放，请重新启动桌宠后重试。");
                _volume = ClampVolume(volume);
                session = new Session(cancellationToken, playbackState);
                _session = session;
            }
            var worker = new Thread(() => Playback(session)) { IsBackground = true, Name = "AI Pet continuous voice" };
            worker.Start();
            Task producer = ProduceAsync(session, source);
            try
            {
                await session.Finished.Task.ConfigureAwait(false);
                await producer.ConfigureAwait(false);
            }
            finally
            {
                session.Cancel();
                try { await producer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            if (session != null)
            {
                lock (_guard) if (ReferenceEquals(_session, session)) _session = null;
                // A stuck driver retains its memory/event ownership until process exit.
                if (!session.RetainNativeState) session.Dispose();
            }
            _playback.Release();
        }
    }

    private static async Task ProduceAsync(Session session, IAsyncEnumerable<PetPcmChunk> source)
    {
        try
        {
            PetPcmFormat? format = null; long bytes = 0;
            byte[]? block = null; int filled = 0;
            await foreach (var chunk in source.WithCancellation(session.Stop.Token).ConfigureAwait(false))
            {
                chunk.Format.Validate();
                if ((format != null && format != chunk.Format) || chunk.Data.Length == 0 ||
                    chunk.Data.Length % (chunk.Format.Channels * 2) != 0 ||
                    (bytes += chunk.Data.Length) > PetPcmProtocol.MaximumAudioBytes)
                    throw new InvalidOperationException("语音流格式改变或超过允许长度。");
                format = chunk.Format;
                // 100 ms blocks allow quick volume changes and a bounded native queue.
                int align = chunk.Format.Channels * 2;
                block ??= new byte[chunk.Format.ByteRate / 10 / align * align];
                for (int offset = 0; offset < chunk.Data.Length;)
                {
                    int size = Math.Min(block.Length - filled, chunk.Data.Length - offset);
                    Array.Copy(chunk.Data, offset, block, filled, size); filled += size; offset += size;
                    if (filled == block.Length)
                    {
                        await session.Audio.Writer.WriteAsync(new PetPcmChunk(chunk.Format, block), session.Stop.Token).ConfigureAwait(false);
                        session.Signal(); block = new byte[block.Length]; filled = 0;
                    }
                }
            }
            if (filled > 0 && block != null && format is { } lastFormat)
            {
                await session.Audio.Writer.WriteAsync(new PetPcmChunk(lastFormat, block.AsSpan(0, filled).ToArray()), session.Stop.Token).ConfigureAwait(false);
                session.Signal();
            }
            session.Audio.Writer.TryComplete(); session.Signal();
        }
        catch (Exception ex)
        {
            session.ProducerError = ex;
            session.Audio.Writer.TryComplete(ex); session.Signal();
        }
    }

    private void Playback(Session session)
    {
        IntPtr output = IntPtr.Zero;
        var pending = new Queue<NativeBuffer>();
        var buffering = new PetPcmBufferPolicy();
        PetPcmFormat? format = null;
        bool ended = false, successful = false, closed = false;
        Exception? failure = null;
        var stalled = Stopwatch.StartNew();
        try
        {
            while (!session.Stop.IsCancellationRequested)
            {
                if (session.ProducerError != null) throw session.ProducerError;
                bool changed = false;
                while (pending.TryPeek(out var first) && first.Done)
                {
                    Check(waveOutUnprepareHeader(output, first.Header, HeaderSize));
                    first.Prepared = false; pending.Dequeue(); buffering.Complete(first.Length); first.Free(); changed = true;
                }
                bool sourceEnded = session.Audio.Reader.Completion.IsCompleted && !session.Audio.Reader.TryPeek(out _);
                // If PCM is already waiting, replenish immediately. A late
                // worker wake-up is not evidence that synthesis fell behind.
                if (buffering.Playing && buffering.QueuedBytes == 0 && !sourceEnded && !session.Audio.Reader.TryPeek(out _))
                {
                    Check(waveOutPause(output)); buffering.Underrun(); session.State(false);
                }
                while (pending.Count < NativeSlots && session.Audio.Reader.TryRead(out var chunk))
                {
                    if (output == IntPtr.Zero)
                    {
                        format = chunk.Format;
                        var wave = new WaveFormat { Tag = 1, Channels = (ushort)chunk.Format.Channels,
                            Rate = (uint)chunk.Format.SampleRate, ByteRate = (uint)chunk.Format.ByteRate,
                            Align = (ushort)(chunk.Format.Channels * 2), Bits = 16 };
                        Check(waveOutOpen(out output, uint.MaxValue, ref wave, session.Wake.SafeWaitHandle.DangerousGetHandle(), UIntPtr.Zero, CallbackEvent));
                        Check(waveOutPause(output));
                    }
                    var buffer = new NativeBuffer(chunk.Data, Volatile.Read(ref _volume));
                    pending.Enqueue(buffer); // Own it even if native preparation/write fails.
                    Check(waveOutPrepareHeader(output, buffer.Header, HeaderSize)); buffer.Prepared = true;
                    Check(waveOutWrite(output, buffer.Header, HeaderSize));
                    buffering.Append(buffer.Length); changed = true;
                }
                ended = session.Audio.Reader.Completion.IsCompleted && !session.Audio.Reader.TryPeek(out _);
                if (ended && session.ProducerError != null) throw session.ProducerError;
                if (format is { } pcm && buffering.ShouldStart(pcm.ByteRate, ended))
                {
                    Check(waveOutRestart(output)); buffering.Start(); session.State(true); changed = true;
                }
                if (ended && pending.Count == 0) { successful = true; break; }
                if (changed) stalled.Restart();
                if (stalled.Elapsed > TimeSpan.FromMinutes(9)) throw new InvalidOperationException("语音流等待超时，请重试。");
                session.Wake.WaitOne(25);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            session.Cancel();
            if (output != IntPtr.Zero)
            {
                bool reset = waveOutReset(output) == 0;
                foreach (var buffer in pending)
                {
                    if (buffer.Prepared && reset && waveOutUnprepareHeader(output, buffer.Header, HeaderSize) == 0) buffer.Prepared = false;
                }
                closed = waveOutClose(output) == 0;
                foreach (var buffer in pending) if (!buffer.Prepared || closed) buffer.Free();
                if (!closed)
                {
                    session.RetainNativeState = true;
                    lock (FailedDevices) FailedDevices.Add(session);
                    Interlocked.Exchange(ref _cleanupFailed, 1);
                    failure ??= new InvalidOperationException("声音设备未能释放，请重新启动桌宠后重试。");
                }
            }
            while (session.Audio.Reader.TryRead(out var chunk)) Array.Clear(chunk.Data);
            if (failure != null) session.Finished.TrySetException(failure);
            else if (successful) session.Finished.TrySetResult();
            else session.Finished.TrySetCanceled();
        }
    }

    public void SetVolume(double volume) => Volatile.Write(ref _volume, ClampVolume(volume));
    private static double ClampVolume(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .6;
    public void Stop() { lock (_guard) _session?.Cancel(); }
    public void Dispose() { lock (_guard) { if (_disposed) return; _disposed = true; _session?.Cancel(); } }
    private static void Check(uint code)
    { if (code != 0) throw new InvalidOperationException("无法播放连续声音，请检查 Windows 默认输出设备。"); }

    private sealed class Session : IDisposable
    {
        internal readonly AutoResetEvent Wake = new(false);
        internal readonly Channel<PetPcmChunk> Audio = Channel.CreateBounded<PetPcmChunk>(new BoundedChannelOptions(48) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        internal readonly CancellationTokenSource Stop;
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Action<bool> State;
        internal volatile Exception? ProducerError;
        internal bool RetainNativeState;
        private readonly CancellationTokenRegistration _registration;
        internal Session(CancellationToken token, Action<bool> state)
        { Stop = CancellationTokenSource.CreateLinkedTokenSource(token); State = state; _registration = Stop.Token.Register(Signal); }
        internal void Signal() { try { Wake.Set(); } catch (ObjectDisposedException) { } }
        internal void Cancel() { try { Stop.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() { _registration.Dispose(); Stop.Dispose(); Wake.Dispose(); }
    }
    private sealed class NativeBuffer
    {
        internal IntPtr Header, Data;
        internal bool Prepared;
        internal readonly int Length;
        internal bool Done => (Marshal.PtrToStructure<WaveHeader>(Header).Flags & HeaderDone) != 0;
        internal NativeBuffer(byte[] bytes, double volume)
        {
            Length = bytes.Length;
            for (int i = 0; i < bytes.Length; i += 2)
            {
                short sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i, 2), (short)(sample * volume));
            }
            Data = Marshal.AllocHGlobal(Length);
            try
            {
                Marshal.Copy(bytes, 0, Data, Length);
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = Data, Length = (uint)Length }, Header, false);
            }
            catch { Free(); throw; }
            finally { Array.Clear(bytes); }
        }
        internal void Free()
        {
            if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); Header = IntPtr.Zero; }
            if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; }
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { internal ushort Tag, Channels; internal uint Rate, ByteRate; internal ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader { internal IntPtr Data; internal uint Length, Recorded; internal UIntPtr User; internal uint Flags, Loops; internal IntPtr Next; internal UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}

internal sealed class PetPcmBufferPolicy
{
    internal long QueuedBytes { get; private set; }
    internal bool Playing { get; private set; }
    internal double StartupSeconds { get; private set; } = 1.0;
    internal void Append(int bytes) => QueuedBytes += bytes;
    internal void Complete(int bytes) => QueuedBytes = Math.Max(0, QueuedBytes - bytes);
    internal bool ShouldStart(int byteRate, bool ended) => !Playing && QueuedBytes > 0 && (ended || QueuedBytes >= byteRate * StartupSeconds);
    internal void Start() => Playing = true;
    internal void Underrun() { Playing = false; StartupSeconds = Math.Min(2, StartupSeconds + .5); }
}
