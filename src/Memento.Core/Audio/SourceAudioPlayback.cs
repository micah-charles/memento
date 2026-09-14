using System.Security.Cryptography;
using Memento.Core.Domain;
using Memento.Core.Storage;
using NAudio.Wave;

namespace Memento.Core.Audio;

public interface ISourceAudioPlayback
{
    Task PlayAsync(SourceMetadata source, CancellationToken cancellationToken = default);
}

/// <summary>Windows playback for an integrity-checked participant Source.</summary>
public sealed class WaveFileSourceAudioPlayback : ISourceAudioPlayback
{
    private readonly string _archiveRoot;

    public WaveFileSourceAudioPlayback(string archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot)) throw new ArgumentException("An archive root is required.", nameof(archiveRoot));
        _archiveRoot = Path.GetFullPath(archiveRoot);
    }

    public async Task PlayAsync(SourceMetadata source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.FilePath))
            throw new FileNotFoundException("The Source has no local audio path.", source.SourceId);
        if (!ArchivePathSafety.IsPathUnderRoot(source.FilePath, _archiveRoot))
            throw new InvalidDataException("The Source audio path is outside the archive directory.");
        ArchivePathSafety.EnsureNoReparsePointInPath(source.FilePath, "The Source audio path");
        if (!File.Exists(source.FilePath))
            throw new FileNotFoundException("The Source audio file was not found.", source.FilePath);

        cancellationToken.ThrowIfCancellationRequested();
        var fileInfo = new FileInfo(source.FilePath);
        if (source.ByteLength is not null && source.ByteLength.Value != fileInfo.Length)
            throw new InvalidDataException("The Source audio length does not match its archived metadata.");
        if (!string.IsNullOrWhiteSpace(source.Sha256))
        {
            using var hashStream = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
            if (!string.Equals(actualHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Source audio failed its archived SHA-256 integrity check.");
        }

        if (!string.Equals(source.Format?.Trim(), "PCM WAV", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(source.FilePath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Local Source playback currently supports PCM WAV files only.");
        PcmWaveValidator.Validate(source.FilePath, allowPartial: false);

        using var reader = new WaveFileReader(source.FilePath);
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
