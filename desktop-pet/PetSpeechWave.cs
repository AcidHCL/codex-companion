using System;
using System.Buffers.Binary;
using System.IO;

namespace DesktopPet;

// The local backend returns PCM16 WAV. Merge only matching PCM payloads, without
// retaining metadata or adding silence between clips. Both buffering and the
// final MediaPlayer file are bounded, including for an unexpectedly long reply.
internal static class PetSpeechWave
{
    internal const int MaximumBufferedBytes = 64 * 1024 * 1024;
    private readonly record struct Format(ushort Channels, uint SampleRate, uint ByteRate, ushort BlockAlign);
    private readonly record struct Clip(Format Format, int Offset, int Length);

    internal static TimeSpan Duration(byte[] wave)
    {
        Clip clip = Parse(wave);
        return TimeSpan.FromSeconds((double)clip.Length / clip.Format.ByteRate);
    }

    private static Clip Parse(byte[] wave)
    {
        if (wave.Length < 44 || wave.Length > MaximumBufferedBytes ||
            !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
            (long)BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4, 4)) + 8 != wave.Length)
            throw InvalidWave();
        Format? format = null;
        int dataOffset = 0, dataLength = 0;
        for (int offset = 12; offset < wave.Length;)
        {
            if (wave.Length - offset < 8) throw InvalidWave();
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(offset + 4, 4));
            long end = (long)offset + 8 + size;
            long next = end + (size & 1);
            if (end > wave.Length || next > wave.Length) throw InvalidWave();
            if (wave.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (format != null || size < 16) throw InvalidWave();
                var payload = wave.AsSpan(offset + 8, 16);
                ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
                uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
                ushort align = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
                if (encoding != 1 || channels is < 1 or > 2 || bits != 16 || sampleRate is < 8000 or > 192000 ||
                    align != channels * 2 || byteRate != sampleRate * align) throw InvalidWave();
                format = new Format(channels, sampleRate, byteRate, align);
            }
            else if (wave.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                if (dataOffset != 0 || size == 0) throw InvalidWave();
                dataOffset = offset + 8; dataLength = (int)size;
            }
            offset = (int)next;
        }
        if (format == null || dataOffset == 0 || dataLength % format.Value.BlockAlign != 0) throw InvalidWave();
        return new Clip(format.Value, dataOffset, dataLength);
    }

    private static InvalidOperationException InvalidWave() => new("语音引擎返回的音频格式无效，暂未播放。");

    internal sealed class Builder : IDisposable
    {
        private MemoryStream? _stream = new();
        private Format? _format;
        private readonly int _maximumBytes;
        internal Builder(int maximumBufferedBytes = MaximumBufferedBytes)
        {
            if (maximumBufferedBytes <= 44 || maximumBufferedBytes > MaximumBufferedBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytes));
            _maximumBytes = maximumBufferedBytes;
            _stream!.SetLength(44);
        }

        internal void Append(byte[] wave)
        {
            var stream = _stream ?? throw new ObjectDisposedException(nameof(Builder));
            Clip clip = Parse(wave);
            if (_format != null && _format != clip.Format) throw new InvalidOperationException("语音片段格式不一致，暂未播放。");
            if (stream.Length + clip.Length > _maximumBytes)
                throw new InvalidOperationException("这条回答的语音过长，已保留文字；请分段朗读。");
            _format = clip.Format;
            stream.Position = stream.Length;
            stream.Write(wave, clip.Offset, clip.Length);
        }

        internal byte[] Complete()
        {
            var stream = _stream ?? throw new ObjectDisposedException(nameof(Builder));
            if (_format is not { } format || stream.Length <= 44) throw InvalidWave();
            var header = stream.GetBuffer().AsSpan(0, 44);
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)stream.Length - 8);
            "WAVEfmt "u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..], format.Channels);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], format.SampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], format.ByteRate);
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..], format.BlockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)stream.Length - 44);
            return stream.ToArray();
        }

        public void Dispose() { _stream?.Dispose(); _stream = null; }
    }
}
