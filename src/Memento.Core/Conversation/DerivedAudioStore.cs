using System.Security.Cryptography;
using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>Stores generated speech separately from participant evidence with an atomic file commit.</summary>
public sealed class DerivedAudioStore
{
    private readonly ArchiveRepository _repository;
    private readonly string _rootDirectory;

    public DerivedAudioStore(ArchiveRepository repository, string rootDirectory)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A derived audio directory is required.", nameof(rootDirectory));
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public DerivedSpeechAsset Store(string sessionId, string? turnId, SpeechOutputResult output, DateTimeOffset? createdAt = null, string? assetId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(output);
        if (output.AudioBytes.Length == 0) throw new ArgumentException("Speech output cannot be empty.", nameof(output));

        var format = NormalizeFormat(output.Format);
        var id = string.IsNullOrWhiteSpace(assetId) ? Guid.NewGuid().ToString("N") : assetId;
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The speech asset ID is not a valid file name.", nameof(assetId));
        var timestamp = createdAt ?? DateTimeOffset.UtcNow;
        var dateDirectory = Path.Combine(_rootDirectory, timestamp.ToUniversalTime().ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), timestamp.ToUniversalTime().ToString("MM", System.Globalization.CultureInfo.InvariantCulture), timestamp.ToUniversalTime().ToString("dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dateDirectory);
        var finalPath = Path.Combine(dateDirectory, id + "." + format);
        var temporaryPath = finalPath + ".speech.tmp";
        var hash = Convert.ToHexString(SHA256.HashData(output.AudioBytes)).ToLowerInvariant();

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(output.AudioBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            var asset = new DerivedSpeechAsset(id, sessionId, turnId, finalPath, format, output.AudioBytes.LongLength, hash, output.Provider, output.Model, output.Voice, output.RequestId, timestamp);
            return _repository.AddDerivedSpeechAsset(asset);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(finalPath);
            throw;
        }
    }

    /// <summary>
    /// Stores raw PCM returned by a Realtime provider as a verified WAV
    /// derived asset. Realtime output is never treated as participant Source
    /// evidence, but it can be replayed through the same safe playback path as
    /// turn-based speech output.
    /// </summary>
    public DerivedSpeechAsset StorePcm(
        string sessionId,
        string? turnId,
        PcmWaveFormat format,
        byte[] pcmBytes,
        string provider,
        string model,
        string? requestId = null,
        DateTimeOffset? createdAt = null,
        string? assetId = null)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("A speech provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A speech model is required.", nameof(model));
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(pcmBytes);
        format.Validate();
        if (pcmBytes.Length == 0) throw new ArgumentException("PCM output cannot be empty.", nameof(pcmBytes));
        if (pcmBytes.LongLength % format.BlockAlign != 0)
            throw new ArgumentException("PCM output must contain complete sample frames.", nameof(pcmBytes));

        using var wav = new MemoryStream(44 + pcmBytes.Length);
        PcmWaveWriter.WriteHeader(wav, format, pcmBytes.LongLength);
        wav.Write(pcmBytes);
        return Store(
            sessionId,
            turnId,
            new SpeechOutputResult(provider, model, "realtime", "wav", requestId, wav.ToArray(), createdAt ?? DateTimeOffset.UtcNow),
            createdAt,
            assetId);
    }

    public byte[] ReadVerified(DerivedSpeechAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var bytes = File.ReadAllBytes(asset.FilePath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != asset.ByteLength || !string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Derived speech output failed its integrity check.");
        return bytes;
    }

    private static string NormalizeFormat(string format)
    {
        var normalized = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Speech output format must be a simple file extension.", nameof(format));
        return normalized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the original storage error; cleanup can be retried by recovery tooling.
        }
    }
}
