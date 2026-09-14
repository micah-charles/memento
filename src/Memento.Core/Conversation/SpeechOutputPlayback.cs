using NAudio.Wave;

namespace Memento.Core.Conversation;

public interface ISpeechOutputPlayback
{
    Task PlayAsync(Domain.DerivedSpeechAsset asset, CancellationToken cancellationToken = default);
}

/// <summary>Windows playback for verified derived WAV output. It never reads participant Source files.</summary>
public sealed class WaveFileSpeechOutputPlayback(DerivedAudioStore store) : ISpeechOutputPlayback
{
    private readonly DerivedAudioStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task PlayAsync(Domain.DerivedSpeechAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!string.Equals(asset.Format, "wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Local speech playback currently supports WAV output only.");

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = _store.ReadVerified(asset);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is null) stopped.TrySetResult();
            else stopped.TrySetException(args.Exception);
        }

        output.PlaybackStopped += OnPlaybackStopped;
        try
        {
            output.Init(reader);
            using var cancellation = cancellationToken.Register(() =>
            {
                // WinMM may raise PlaybackStopped synchronously from Stop().
                // Mark cancellation first so that event cannot win the race
                // and make an interrupted playback look successful.
                stopped.TrySetCanceled(cancellationToken);
                try { output.Stop(); } catch { }
            });
            output.Play();
            await stopped.Task.ConfigureAwait(false);
        }
        finally
        {
            output.PlaybackStopped -= OnPlaybackStopped;
        }
    }
}
