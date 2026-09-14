using System.Buffers.Binary;
using System.Security.Cryptography;
using Memento.Core.Storage;

namespace Memento.Core.Audio;

public sealed record PcmWaveFormat(int SampleRate, short Channels, short BitsPerSample)
{
    public short BlockAlign => checked((short)(Channels * (BitsPerSample / 8)));
    public int ByteRate => checked(SampleRate * BlockAlign);

    public void Validate()
    {
        if (SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (Channels <= 0) throw new ArgumentOutOfRangeException(nameof(Channels));
        if (BitsPerSample is not (8 or 16 or 24 or 32)) throw new ArgumentOutOfRangeException(nameof(BitsPerSample));
    }
}

public sealed record FinalizedAudioAsset(
    string SourceId,
    string FilePath,
    PcmWaveFormat Format,
    long ByteLength,
    long DurationMs,
    string Sha256,
    DateTimeOffset StartedAt,
    DateTimeOffset FinalizedAt);

public sealed record RecoverableAudioAsset(
    string TemporaryPath,
    long ByteLength,
    long DurationMs,
    bool IsValidPcm,
    string? Error);

public sealed class PcmWaveWriter : IDisposable
{
    private const int HeaderLength = 44;
    private readonly FileStream _stream;
    private readonly string _temporaryPath;
    private readonly string _finalPath;
    private readonly string _sourceId;
    private readonly PcmWaveFormat _format;
    private readonly DateTimeOffset _startedAt;
    private long _dataBytes;
    private bool _closed;

    private PcmWaveWriter(string temporaryPath, string finalPath, string sourceId, PcmWaveFormat format, DateTimeOffset startedAt)
    {
        _temporaryPath = temporaryPath;
        _finalPath = finalPath;
        _sourceId = sourceId;
        _format = format;
        _startedAt = startedAt;
        _stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
        WriteHeader(0);
        Flush();
    }

    public string SourceId => _sourceId;
    public string TemporaryPath => _temporaryPath;
    public string FinalPath => _finalPath;
    public long DataBytes => _dataBytes;

    public static PcmWaveWriter Create(string audioRootDirectory, string sessionId, DateTimeOffset startedAt, PcmWaveFormat format, string? sourceId = null, string? turnId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        ValidatePathComponent(sessionId, nameof(sessionId));
        if (turnId is not null) ValidatePathComponent(turnId, nameof(turnId));
        format.Validate();
        var id = sourceId ?? Guid.NewGuid().ToString("N");
        ValidatePathComponent(id, nameof(sourceId));
        var dateDirectory = Path.Combine(audioRootDirectory, startedAt.ToUniversalTime().ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), startedAt.ToUniversalTime().ToString("MM", System.Globalization.CultureInfo.InvariantCulture), startedAt.ToUniversalTime().ToString("dd", System.Globalization.CultureInfo.InvariantCulture));
        EnsureNoReparsePointInPath(dateDirectory, "The audio capture path");
        Directory.CreateDirectory(dateDirectory);
        EnsureNoReparsePointInPath(dateDirectory, "The audio capture path");
        var stem = turnId is null ? $"{sessionId}-{id}" : $"{sessionId}-{turnId}-{id}";
        var finalPath = Path.Combine(dateDirectory, stem + ".wav");
        var temporaryPath = finalPath + ".capture.tmp";
        return new PcmWaveWriter(temporaryPath, finalPath, id, format, startedAt);
    }

    public void Append(ReadOnlySpan<byte> pcmBytes)
    {
        ThrowIfClosed();
        if (pcmBytes.Length % _format.BlockAlign != 0)
            throw new ArgumentException("PCM data must contain complete sample frames.", nameof(pcmBytes));
        try
        {
            _stream.Write(pcmBytes);
        }
        finally
        {
            // A storage failure can occur after a partial write. Derive the
            // durable count from the file itself so Dispose() can preserve
            // every byte that reached the recovery marker.
            _dataBytes = Math.Max(_dataBytes, Math.Max(0, _stream.Length - HeaderLength));
        }
        // Keep the recovery marker self-describing while capture is active.
        // A process interruption can therefore be scanned using the bytes
        // already flushed instead of relying on Dispose() to repair the WAV
        // header later.
        WriteHeader(_dataBytes);
        Flush();
    }

    public FinalizedAudioAsset FinalizeAsset()
    {
        ThrowIfClosed();
        try
        {
            WriteHeader(_dataBytes);
            Flush();
            _stream.Dispose();
            _closed = true;
            PcmWaveValidator.Validate(_temporaryPath, allowPartial: false);
            var hash = ComputeSha256(_temporaryPath);
            File.Move(_temporaryPath, _finalPath, overwrite: false);
            var finalizedAt = DateTimeOffset.UtcNow;
            return new FinalizedAudioAsset(_sourceId, _finalPath, _format, HeaderLength + _dataBytes, Duration(_dataBytes), hash, _startedAt, finalizedAt);
        }
        catch
        {
            if (!_closed)
            {
                try { _stream.Dispose(); }
                finally { _closed = true; }
            }

            throw;
        }
    }

    /// <summary>
    /// Moves a successfully finalized file back to its recovery marker when a
    /// later archive-registration step fails. The bytes remain intact and the
    /// next recovery scan can surface the asset for review.
    /// </summary>
    public void RestoreFinalizedAssetForRecovery()
    {
        if (!_closed)
            throw new InvalidOperationException("The WAV writer must be finalized before restoring an asset for recovery.");
        if (!File.Exists(_finalPath)) return;
        if (File.Exists(_temporaryPath))
            throw new IOException("The recovery marker already exists.");
        File.Move(_finalPath, _temporaryPath, overwrite: false);
    }

    public void Dispose()
    {
        if (_closed) return;
        try { WriteHeader(_dataBytes); Flush(); }
        finally
        {
            _stream.Dispose();
            _closed = true;
        }
    }

    private void Flush() => _stream.Flush(flushToDisk: true);

    internal static void WriteHeader(Stream stream, PcmWaveFormat format, long dataBytes)
    {
        if (dataBytes > uint.MaxValue) throw new InvalidOperationException("WAV data exceeds the RIFF size limit.");
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], checked((uint)(36 + dataBytes)));
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], checked((ushort)format.Channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], checked((uint)format.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], checked((uint)format.ByteRate));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], checked((ushort)format.BlockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], checked((ushort)format.BitsPerSample));
        "data"u8.CopyTo(header[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], checked((uint)dataBytes));
        stream.Position = 0;
        stream.Write(header);
        stream.Position = stream.Length;
    }

    private void WriteHeader(long dataBytes) => WriteHeader(_stream, _format, dataBytes);

    private long Duration(long bytes) => bytes * 1000L / _format.ByteRate;
    private static string ComputeSha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private void ThrowIfClosed() { if (_closed) throw new ObjectDisposedException(nameof(PcmWaveWriter)); }

    private static void ValidatePathComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The identifier must be a single safe file-name component.", parameterName);
    }

    private static void EnsureNoReparsePointInPath(string path, string description)
        => ArchivePathSafety.EnsureNoReparsePointInPath(path, description);
}

public static class PcmWaveValidator
{
    public static PcmWaveFormat Validate(string path, bool allowPartial)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("WAV file not found.", path);
        using var stream = File.OpenRead(path);
        if (stream.Length < 44) throw new InvalidDataException("WAV header is incomplete.");
        Span<byte> header = stackalloc byte[44];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8) || !header[12..16].SequenceEqual("fmt "u8) || !header[36..40].SequenceEqual("data"u8))
            throw new InvalidDataException("Unsupported WAV container.");
        var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]);
        if (audioFormat != 1) throw new InvalidDataException("WAV is not PCM.");
        var channels = checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[22..24]));
        var sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]));
        var bits = checked((short)BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]));
        var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
        if (dataBytes > stream.Length - 44) throw new InvalidDataException("WAV data chunk is truncated.");
        if (!allowPartial && dataBytes != stream.Length - 44) throw new InvalidDataException("WAV data chunk length does not match file length.");
        var format = new PcmWaveFormat(sampleRate, channels, bits);
        format.Validate();
        if (dataBytes % format.BlockAlign != 0) throw new InvalidDataException("WAV data is not aligned to a PCM sample frame.");
        return format;
    }

    public static long GetDataBytes(string path)
    {
        using var stream = File.OpenRead(path);
        stream.Position = 40;
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}

public static class AudioRecoveryScanner
{
    public static IReadOnlyList<RecoverableAudioAsset> Scan(string audioRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(audioRootDirectory)) return [];
        var root = Path.GetFullPath(audioRootDirectory);
        if (!Directory.Exists(root) || IsReparsePoint(root)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*.capture.tmp", options)
            .Select(path => Inspect(path))
            .ToArray();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static RecoverableAudioAsset Inspect(string path)
    {
        try
        {
            var format = PcmWaveValidator.Validate(path, allowPartial: true);
            var declaredBytes = PcmWaveValidator.GetDataBytes(path);
            var availableBytes = Math.Max(0, new FileInfo(path).Length - 44);
            // A hard interruption can leave flushed PCM behind a stale WAV
            // data-length field. When the available bytes form complete PCM
            // frames, recover that larger extent without rewriting the marker.
            var bytes = availableBytes >= declaredBytes && availableBytes % format.BlockAlign == 0
                ? availableBytes
                : declaredBytes;
            return new RecoverableAudioAsset(path, new FileInfo(path).Length, bytes * 1000L / format.ByteRate, true, null);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            return new RecoverableAudioAsset(path, new FileInfo(path).Length, 0, false, error.Message);
        }
    }
}
