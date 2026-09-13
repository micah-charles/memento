using NAudio.Wave;

namespace Memento.Core.Audio;

/// <summary>Windows native wave-in adapter. It exposes PCM frames through the provider-neutral capture contract.</summary>
public sealed class WaveInAudioInput : IAudioInput
{
    private readonly WaveInEvent _capture;
    private bool _started;

    public WaveInAudioInput(PcmWaveFormat requestedFormat)
    {
        requestedFormat.Validate();
        _capture = new WaveInEvent
        {
            BufferMilliseconds = 100,
            WaveFormat = new WaveFormat(requestedFormat.SampleRate, requestedFormat.BitsPerSample, requestedFormat.Channels)
        };
        Format = new PcmWaveFormat(_capture.WaveFormat.SampleRate, (short)_capture.WaveFormat.Channels, (short)_capture.WaveFormat.BitsPerSample);
        _capture.DataAvailable += HandleDataAvailable;
        _capture.RecordingStopped += HandleRecordingStopped;
    }

    public PcmWaveFormat Format { get; }
    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    public event EventHandler<Exception>? CaptureError;

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Audio input is already active.");
        _capture.StartRecording();
        _started = true;
    }

    public void Stop()
    {
        if (!_started) return;
        _capture.StopRecording();
        _started = false;
    }

    public void Dispose()
    {
        if (_started)
        {
            try { _capture.StopRecording(); } catch { }
        }
        _capture.Dispose();
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
        => DataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded));

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            CaptureError?.Invoke(this, e.Exception);
    }
}
