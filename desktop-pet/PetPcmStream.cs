using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet;

internal readonly record struct PetPcmFormat(int SampleRate, int Channels)
{
    internal int ByteRate => checked(SampleRate * Channels * 2);
    internal void Validate()
    {
        if (SampleRate is < 8000 or > 48000 || Channels is < 1 or > 2)
            throw new InvalidOperationException("语音流音频格式无效。");
    }
}
internal sealed record PetPcmChunk(PetPcmFormat Format, byte[] Data);
internal interface IPetStreamingSpeechSynthesizer
{
    bool SupportsStreaming => true;
    IAsyncEnumerable<PetPcmChunk> StreamAsync(string text, string requestId, CancellationToken cancellationToken);
}
internal interface IPetVoiceLifecycle
{
    Task PreloadAsync(string requestId, CancellationToken cancellationToken);
    Task SetRetentionAsync(bool active, CancellationToken cancellationToken);
    Task UnloadAsync(CancellationToken cancellationToken);
}
internal interface IPetPcmStreamPlayer : IDisposable
{
    Task PlayAsync(IAsyncEnumerable<PetPcmChunk> source, double volume, Action<bool> playbackState, CancellationToken cancellationToken);
    void SetVolume(double volume);
    void Stop();
}

// Bounded records and an explicit end marker distinguish a complete response
// from a socket that disappeared midway through speech.
internal static class PetPcmProtocol
{
    internal const int MaximumRecordBytes = 2 * 1024 * 1024;
    internal const int MaximumAudioBytes = 64 * 1024 * 1024;
    internal static async IAsyncEnumerable<PetPcmChunk> ReadAsync(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[16384];
        using var record = new MemoryStream();
        PetPcmFormat? format = null;
        long total = 0; bool done = false, heard = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            for (int i = 0; i < read; i++)
            {
                byte value = buffer[i];
                if (value != 10)
                {
                    if (record.Length >= MaximumRecordBytes) throw new InvalidOperationException("语音流片段超过允许大小。");
                    record.WriteByte(value); continue;
                }
                if (done) throw new InvalidOperationException("语音流结束后仍有数据。");
                using var json = JsonDocument.Parse(record.GetBuffer().AsMemory(0, (int)record.Length), new JsonDocumentOptions { MaxDepth = 5 });
                record.SetLength(0);
                var root = json.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (type == "format")
                {
                    if (format != null || root.GetProperty("sample_format").GetString() != "s16le") throw new InvalidOperationException("语音流音频格式无效。");
                    var next = new PetPcmFormat(root.GetProperty("sample_rate").GetInt32(), root.GetProperty("channels").GetInt32());
                    next.Validate(); format = next;
                }
                else if (type == "audio")
                {
                    if (format is not { } pcmFormat) throw new InvalidOperationException("语音流缺少音频格式。");
                    byte[] data = root.GetProperty("data").GetBytesFromBase64();
                    if (data.Length == 0 || data.Length % (pcmFormat.Channels * 2) != 0 || (total += data.Length) > MaximumAudioBytes)
                        throw new InvalidOperationException("语音流片段无效或超过允许长度。");
                    heard = true; yield return new PetPcmChunk(pcmFormat, data);
                }
                else if (type == "done")
                {
                    if (format == null || !heard) throw new InvalidOperationException("语音引擎返回了空音频。");
                    done = true;
                }
                else if (type == "error") throw new InvalidOperationException("本地流式语音合成中断，请重试。");
                else throw new InvalidOperationException("语音流返回未知数据。");
            }
        }
        if (!done || record.Length != 0) throw new InvalidOperationException("语音流传输中断，声音未完整生成。");
    }
}
