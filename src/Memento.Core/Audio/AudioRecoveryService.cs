using System.Security.Cryptography;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Audio;

/// <summary>
/// Promotes a valid interrupted capture into a normal archive Source without
/// discarding the original bytes or guessing a transcript.
/// </summary>
public sealed class AudioRecoveryService
{
    private const int HeaderLength = 44;
    private readonly ArchiveRepository _repository;
    private readonly string _audioRootDirectory;

    public AudioRecoveryService(ArchiveRepository repository, string audioRootDirectory)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audioRootDirectory = Path.GetFullPath(audioRootDirectory ?? throw new ArgumentNullException(nameof(audioRootDirectory)));
    }

    /// <summary>
    /// Capture markers created by MEMENTO contain the generated session ID and
    /// source ID in the file stem. Only the generated 32-character session ID
    /// form is inferred; callers must provide an explicit ID for imported or
    /// hand-created markers.
    /// </summary>
    public static bool TryInferSessionId(string temporaryPath, out string sessionId)
    {
        return TryInferCaptureContext(temporaryPath, out sessionId, out _);
    }

    public static bool TryInferCaptureContext(string temporaryPath, out string sessionId, out string? turnId)
    {
        sessionId = string.Empty;
        turnId = null;
        var name = Path.GetFileName(temporaryPath);
        const string suffix = ".wav.capture.tmp";
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^suffix.Length];
        var parts = stem.Split('-');
        if (parts.Length < 2 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _)) return false;
        sessionId = parts[0];
        if (parts.Length >= 3 && parts[1].Length == 32 && Guid.TryParseExact(parts[1], "N", out _))
            turnId = parts[1];
        return true;
    }

    public SourceMetadata Recover(string temporaryPath, string sessionId, string? turnId = null, DateTimeOffset? startedAt = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (_repository.GetSession(sessionId) is null) throw new InvalidDataException("The recovery session was not found.");

        var fullTemporaryPath = Path.GetFullPath(temporaryPath ?? throw new ArgumentNullException(nameof(temporaryPath)));
        EnsurePathIsUnderAudioRoot(fullTemporaryPath);
        if (!fullTemporaryPath.EndsWith(".wav.capture.tmp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The recovery path must be a .wav.capture.tmp marker.", nameof(temporaryPath));
        if (!File.Exists(fullTemporaryPath)) throw new FileNotFoundException("The recovery marker was not found.", fullTemporaryPath);

        var format = PcmWaveValidator.Validate(fullTemporaryPath, allowPartial: true);
        var effectiveTurnId = turnId;
        if (TryInferCaptureContext(fullTemporaryPath, out var inferredSessionId, out var inferredTurnId))
        {
            if (!string.Equals(inferredSessionId, sessionId, StringComparison.Ordinal))
                throw new InvalidDataException("The recovery marker belongs to a different session.");
            effectiveTurnId ??= inferredTurnId;
        }
        var availableBytes = new FileInfo(fullTemporaryPath).Length - HeaderLength;
        if (availableBytes <= 0 || availableBytes % format.BlockAlign != 0)
            throw new InvalidDataException("The recovery marker does not contain complete PCM frames.");

        var sourceId = Guid.NewGuid().ToString("N");
        var directory = Path.GetDirectoryName(fullTemporaryPath)!;
        var originalStem = Path.GetFileName(fullTemporaryPath)[..^".wav.capture.tmp".Length];
        var finalPath = Path.Combine(directory, $"{originalStem}-recovered-{sourceId}.wav");
        var stagingPath = fullTemporaryPath + ".recovery-" + Guid.NewGuid().ToString("N");
        var recoveredAt = DateTimeOffset.UtcNow;
        var effectiveStartedAt = startedAt ?? new DateTimeOffset(File.GetCreationTimeUtc(fullTemporaryPath), TimeSpan.Zero);

        try
        {
            using (var input = new FileStream(fullTemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                PcmWaveWriter.WriteHeader(output, format, availableBytes);
                input.Position = HeaderLength;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            PcmWaveValidator.Validate(stagingPath, allowPartial: false);
            // Replace the marker with the repaired, self-describing WAV first.
            // A crash before the final move therefore leaves a valid marker for
            // the next startup scan.
            File.Replace(stagingPath, fullTemporaryPath, null);
            File.Move(fullTemporaryPath, finalPath, overwrite: false);

            var source = new SourceMetadata(
                sourceId,
                "audio",
                sessionId,
                effectiveTurnId,
                finalPath,
                "PCM WAV",
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                new FileInfo(finalPath).Length,
                availableBytes * 1000L / format.ByteRate,
                ComputeSha256(finalPath),
                effectiveStartedAt,
                recoveredAt,
                "recovered",
                recoveredAt);
            try
            {
                return _repository.AddSource(source);
            }
            catch
            {
                // Keep the bytes discoverable if the database registration is
                // interrupted or unavailable. The marker is now valid PCM.
                if (File.Exists(finalPath) && !File.Exists(fullTemporaryPath))
                    File.Move(finalPath, fullTemporaryPath, overwrite: false);
                throw;
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    private void EnsurePathIsUnderAudioRoot(string path)
    {
        var root = _audioRootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The recovery marker must be inside the archive audio directory.", nameof(path));

        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The recovery marker path cannot contain a reparse point.");
                }
                catch (UnauthorizedAccessException error)
                {
                    throw new IOException("The recovery marker path cannot be inspected safely.", error);
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
