using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Audio;

public sealed class ConsentRequiredException() : InvalidOperationException("Local recording requires explicit local-capture consent.");

public enum AudioCaptureState
{
    Idle,
    Capturing,
    Finalized,
    Recoverable,
    Failed
}

public sealed class AudioDataEventArgs(byte[] buffer, int bytesRecorded) : EventArgs
{
    public byte[] Buffer { get; } = buffer;
    public int BytesRecorded { get; } = bytesRecorded;
}

public interface IAudioInput : IDisposable
{
    PcmWaveFormat Format { get; }
    event EventHandler<AudioDataEventArgs>? DataAvailable;
    event EventHandler<Exception>? CaptureError;
    void Start();
    void Stop();
}

public sealed class AudioCaptureController
{
    private readonly ArchiveRepository _repository;
    private readonly string _audioRootDirectory;
    private PcmWaveWriter? _writer;
    private IAudioInput? _input;
    private string? _sessionId;
    private string? _turnId;

    public AudioCaptureController(ArchiveRepository repository, string audioRootDirectory)
    {
        _repository = repository;
        _audioRootDirectory = audioRootDirectory;
    }

    public AudioCaptureState State { get; private set; } = AudioCaptureState.Idle;
    public string? Failure { get; private set; }
    public event EventHandler<Exception>? CaptureFailed;

    public void Start(string sessionId, string? turnId, bool localCaptureConsent, Func<PcmWaveFormat, IAudioInput> inputFactory, DateTimeOffset? startedAt = null)
    {
        if (!localCaptureConsent) throw new ConsentRequiredException();
        if (State == AudioCaptureState.Capturing) throw new InvalidOperationException("Capture is already active.");
        _sessionId = sessionId;
        _turnId = turnId;
        IAudioInput? input = null;
        try
        {
            input = inputFactory(new PcmWaveFormat(48000, 1, 16)) ?? throw new InvalidOperationException("Audio input factory returned no input.");
            _input = input;
            _writer = PcmWaveWriter.Create(_audioRootDirectory, sessionId, startedAt ?? DateTimeOffset.UtcNow, input.Format);
            input.DataAvailable += OnDataAvailable;
            input.CaptureError += OnCaptureError;
            State = AudioCaptureState.Capturing;
            input.Start();
            if (State == AudioCaptureState.Failed)
                throw new InvalidOperationException(Failure ?? "Audio input failed during start.");
        }
        catch (Exception error)
        {
            if (State != AudioCaptureState.Failed)
                FailCapture(error);
            throw;
        }
    }

    public SourceMetadata Stop()
    {
        if (State != AudioCaptureState.Capturing || _writer is null || _input is null || _sessionId is null)
            throw new InvalidOperationException("Capture is not active.");
        try { _input.Stop(); }
        catch (Exception error)
        {
            OnCaptureError(_input, error);
            throw;
        }
        _input.DataAvailable -= OnDataAvailable;
        _input.CaptureError -= OnCaptureError;
        try
        {
            _input.Dispose();
        }
        catch (Exception error)
        {
            FailCapture(error);
            throw;
        }
        _input = null;
        try
        {
            var asset = _writer.FinalizeAsset();
            _writer.Dispose();
            _writer = null;
            var source = new SourceMetadata(asset.SourceId, "audio", _sessionId, _turnId, asset.FilePath, "PCM WAV", asset.Format.SampleRate, asset.Format.Channels, asset.Format.BitsPerSample, asset.ByteLength, asset.DurationMs, asset.Sha256, asset.StartedAt, asset.FinalizedAt, "finalized", DateTimeOffset.UtcNow);
            _repository.AddSource(source);
            State = AudioCaptureState.Finalized;
            return source;
        }
        catch (Exception error)
        {
            var writer = _writer;
            _writer = null;
            try { writer?.Dispose(); } catch { }
            FailCapture(error);
            throw;
        }
    }

    public void AbortForRecovery()
    {
        if (_input is not null)
        {
            _input.DataAvailable -= OnDataAvailable;
            _input.CaptureError -= OnCaptureError;
            _input.Dispose();
            _input = null;
        }
        _writer?.Dispose();
        _writer = null;
        State = AudioCaptureState.Recoverable;
    }

    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        try { _writer?.Append(e.Buffer.AsSpan(0, e.BytesRecorded)); }
        catch (Exception error) { OnCaptureError(sender, error); }
    }

    private void OnCaptureError(object? sender, Exception error)
    {
        FailCapture(error);
    }

    private void FailCapture(Exception error)
    {
        if (State == AudioCaptureState.Failed) return;
        Failure = error.Message;
        if (_input is not null)
        {
            _input.DataAvailable -= OnDataAvailable;
            _input.CaptureError -= OnCaptureError;
            try { _input.Dispose(); } catch { }
            _input = null;
        }
        var writer = _writer;
        _writer = null;
        try { writer?.Dispose(); } catch { }
        State = AudioCaptureState.Failed;
        CaptureFailed?.Invoke(this, error);
    }
}
