using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace DesktopPet;

internal interface IPetVoiceStudio : IDisposable
{
    Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct);
    Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct);
    void StopPlayback();
    PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null);
    PetSavedVoiceInfo? ReadSavedVoice(string skinId);
    IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId);
    PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name);
    void DeleteSavedVoice(string referenceId);
}

// Candidate audio stays in memory until the explicit save. Generation never
// changes character preferences, and the temporary worker is always retired.
internal sealed class PetVoiceStudio : IPetVoiceStudio
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<CancellationToken, Task> _begin;
    private readonly Action _end;
    private readonly Func<IPetSpeechSynthesizer> _createSynthesizer;
    private readonly IPetSpeechPlayer _player;
    private readonly PetVoiceLibrary _library;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private IPetSpeechSynthesizer? _activeSynthesizer;
    private CancellationTokenSource? _playback;
    private bool _disposed;

    internal PetVoiceStudio(PetWindow host) : this(host.Dispatcher, host.BeginVoiceStudioAsync, host.EndVoiceStudio,
        () => new PetVoiceClient(port: AvailablePort()), new PetSpeechPlayer(host.Dispatcher), new PetVoiceLibrary()) { }

    internal PetVoiceStudio(Dispatcher dispatcher, Func<CancellationToken, Task> begin, Action end,
        Func<IPetSpeechSynthesizer> createSynthesizer, IPetSpeechPlayer player, PetVoiceLibrary library)
    {
        _dispatcher = dispatcher; _begin = begin; _end = end;
        _createSynthesizer = createSynthesizer; _player = player; _library = library;
    }

    private static int AvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    internal static void ValidateInput(string skinId, string instruction, string text, string language)
    {
        if (!PetVoiceProfiles.KnownSkin(skinId)) throw new InvalidOperationException("请选择一个桌宠角色。");
        if (string.IsNullOrWhiteSpace(instruction) || instruction.Length > 600 || !ValidUnicode(instruction) || instruction.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            throw new InvalidOperationException("请填写 1–600 字的音色描述。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100 || !ValidUnicode(text) || text.Any(c => char.IsControl(c)))
            throw new InvalidOperationException("试听台词须为 1–100 字的单段文字。");
        if (!PetVoiceProfiles.Languages.Contains(language)) throw new InvalidOperationException("请选择支持的试听语言。");
    }

    private static bool ValidUnicode(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (char.IsHighSurrogate(text[i]))
            { if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i])) return false;
        return true;
    }

    public async Task<PetVoiceDesignSample> GenerateAsync(string skinId, string instruction, string text, string language, CancellationToken ct)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateInput(skinId, instruction, text, language);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromMinutes(8));
        var token = cancellation.Token;
        await _operations.WaitAsync(token);
        bool began = false;
        IPetSpeechSynthesizer? synthesizer = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _begin(token); began = true;
            token.ThrowIfCancellationRequested();
            synthesizer = _createSynthesizer(); _activeSynthesizer = synthesizer;
            if (synthesizer is not IPetVoiceProfileSelector selector)
                throw new InvalidOperationException("当前语音引擎不支持文字设计音色。");
            await selector.SelectProfileAsync(new("voice-design", "Vivian", language, instruction.Trim()), token);
            byte[] wave = await synthesizer.SynthesizeAsync(text.Trim(), Guid.NewGuid().ToString("N"), token);
            token.ThrowIfCancellationRequested();
            var sample = new PetVoiceDesignSample(skinId.ToLowerInvariant(), instruction.Trim(), text.Trim(), language, wave);
            PetVoiceLibrary.ValidateSample(sample);
            return sample;
        }
        finally
        {
            try
            {
                if (synthesizer != null)
                {
                    try { await synthesizer.ReleaseAsync(); }
                    finally { synthesizer.Dispose(); }
                }
            }
            finally
            {
                if (ReferenceEquals(_activeSynthesizer, synthesizer)) _activeSynthesizer = null;
                if (began) _end();
                _operations.Release();
            }
        }
    }

    public async Task PlayAsync(PetVoiceDesignSample sample, double volume, CancellationToken ct)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        PetVoiceLibrary.ValidateSample(sample);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _operations.WaitAsync(cancellation.Token);
        bool began = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playback = cancellation;
            await _begin(cancellation.Token); began = true;
            cancellation.Token.ThrowIfCancellationRequested();
            // Replay the exact candidate, never synthesize again for audition.
            await _player.PlayAsync(sample.Wave, volume, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_playback, cancellation)) _playback = null;
            if (began) _end();
            _operations.Release();
        }
    }

    public void StopPlayback()
    {
        _dispatcher.VerifyAccess();
        _playback?.Cancel();
        _player.Stop();
    }

    public PetVoiceProfile Save(PetVoiceDesignSample sample, string? name = null)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _library.Save(sample, name);
    }

    public PetSavedVoiceInfo? ReadSavedVoice(string skinId)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _library.FindLatestForSkin(skinId);
    }

    public IReadOnlyList<PetSavedVoiceInfo> ListSavedVoices(string skinId)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _library.ListForSkin(skinId);
    }

    public PetSavedVoiceInfo RenameSavedVoice(string referenceId, string name)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _library.Rename(referenceId, name);
    }

    public void DeleteSavedVoice(string referenceId)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _library.Delete(referenceId);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        StopPlayback();
        // Kill only the app-owned temporary worker even if model loading cannot
        // observe cancellation until it returns. Async finally releases the lease.
        _activeSynthesizer?.Dispose();
        _player.Dispose();
    }
}
