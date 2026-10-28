using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DesktopPet;

// Only format/memory/ABI checks and read-only device enumeration. Never records.
internal static class PetMicrophoneChecks
{
    internal static int Run()
    {
        var checks = new List<string>(); string? error = null; uint devices = 0;
        void Require(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            Require(Marshal.SizeOf<PetMicrophoneRecorder.WaveFormat>() == 18, "waveformatex_native_abi_is_18_bytes");
            Require(Marshal.SizeOf<PetMicrophoneRecorder.WaveHeader>() == (IntPtr.Size == 8 ? 48 : 32), "wavehdr_native_pointer_layout_matches_process");
            var audio = new PetMicrophoneRecorder.PcmAudioBuffer();
            var pcm = new byte[3200];
            for (int i = 0; i < pcm.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i, 2), (short)(i - 1600));
            audio.Append(pcm);
            byte[] wave = audio.ToWave();
            Require(wave.Length == 3244 && Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt " && Encoding.ASCII.GetString(wave, 36, 4) == "data", "capture_is_standard_pcm_wav_with_correct_chunks");
            Require(BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)) == wave.Length - 8 && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)) == pcm.Length, "riff_and_data_lengths_match_payload");
            Require(BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20)) == 1 && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)) == 1 && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)) == 16000 && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28)) == 32000 && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32)) == 2 && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)) == 16, "capture_format_is_16khz_mono_pcm16");
            Require(wave.AsSpan(44).SequenceEqual(pcm), "recorded_sample_order_and_amplitude_preserved");
            audio.Append(new byte[PetMicrophoneRecorder.MaximumPcmBytes]);
            audio.Append(new byte[4]);
            Require(audio.Length == 1920000 && audio.ToWave().Length == 1920044, "recording_is_hard_bounded_at_60_seconds");
            audio.Clear(); audio.Append(new byte[] { 7, 8, 9 });
            Require(audio.Length == 2 && audio.ToWave().AsSpan(44).SequenceEqual(new byte[] { 7, 8 }), "tail_remains_sample_aligned_and_old_capture_is_not_reused");
            audio.Clear(); Require(audio.ToWave().Length == 44, "cancellation_can_discard_all_audio");
            bool rejected = false;
            try { PetMicrophoneRecorder.PcmAudioBuffer.BuildWave(new byte[3]); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "malformed_odd_pcm_is_rejected");
            using var recorder = new PetMicrophoneRecorder();
            recorder.Cancel(); recorder.Dispose(); recorder.Dispose();
            Require(!recorder.IsRecording, "unused_recorder_cleanup_is_idempotent_without_opening_device");
            devices = PetMicrophoneRecorder.GetAvailableDeviceCount();
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "microphone-validation.json"), JsonSerializer.Serialize(new
        { passed = error == null, assertions = checks.Count, checks, error, availableInputDevices = devices, microphoneOpened = false, audioRecorded = false }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }
}
