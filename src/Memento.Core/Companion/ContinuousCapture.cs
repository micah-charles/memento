using Memento.Core.Audio;
using Memento.Core.Domain;

namespace Memento.Core.Companion;

public sealed record CapturedChunk(byte[] Pcm, long StartSample);

/// <summary>Keeps the input device open across file rotation and AI playback.</summary>
public sealed class ContinuousCapture : IDisposable
{
    public static readonly PcmWaveFormat Format = new(48000, 1, 16);
    private readonly CompanionArchive _archive;
    private readonly string _root;
    private readonly string _session;
    private readonly int _segmentSeconds;
    private readonly object _gate = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly List<(string Id, long Start, long Count)> _chunks = [];
    private PcmWaveWriter? _writer;
    private IAudioInput? _input;
    private long _position;
    private long _segmentStart;
    public event Action<CapturedChunk>? Chunk;
    public event Action<Exception>? Failed;
    public long Position { get { lock (_gate) return _position; } }

    public ContinuousCapture(CompanionArchive archive, string root, string session, int segmentSeconds = 60)
    { _archive = archive; _root = root; _session = session; _segmentSeconds = segmentSeconds > 0 ? segmentSeconds : throw new ArgumentOutOfRangeException(nameof(segmentSeconds)); }

    public void Start(IAudioInput input)
    {
        if (input.Format != Format) throw new InvalidDataException("Capture requires 48kHz mono PCM16.");
        if (!_archive.Repository.HasGrantedConsent(_session, ConsentScope.LocalCapture)) throw new ConsentRequiredException();
        _input = input;
        input.DataAvailable += OnData;
        input.CaptureError += OnError;
        input.Start();
    }
    private void OnError(object? sender, Exception e) => Failed?.Invoke(e);
    private void OnData(object? sender, AudioDataEventArgs e)
    {
        try { Append(e.Buffer.AsSpan(0, e.BytesRecorded)); }
        catch (Exception error) { Failed?.Invoke(error); }
    }
    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0) throw new InvalidDataException("Incomplete PCM sample.");
        long start;
        lock (_gate)
        {
            start = _position;
            var remaining = pcm;
            while (!remaining.IsEmpty)
            {
                _writer ??= PcmWaveWriter.Create(_root, _session, _started.AddSeconds((double)_position / Format.SampleRate), Format);
                var capacity = (long)_segmentSeconds * Format.ByteRate - _writer.DataBytes;
                var take = (int)Math.Min(capacity, remaining.Length);
                _writer.Append(remaining[..take]);
                remaining = remaining[take..]; _position += take / 2;
                if (_writer.DataBytes == (long)_segmentSeconds * Format.ByteRate) FlushSegmentCore();
            }
        }
        Chunk?.Invoke(new(pcm.ToArray(), start));
    }
    public void FlushSegment() { lock (_gate) FlushSegmentCore(); }
    private void FlushSegmentCore()
    {
        if (_writer is null) return;
        var writer = _writer;
        var asset = writer.FinalizeAsset();
        try
        {
            var count = writer.DataBytes / Format.BlockAlign;
            var source = new SourceMetadata(asset.SourceId, "audio", _session, null, asset.FilePath, "PCM WAV", 48000, 1, 16, asset.ByteLength, asset.DurationMs, asset.Sha256, asset.StartedAt, asset.FinalizedAt, "finalized", DateTimeOffset.UtcNow);
            _archive.Repository.AddSource(source);
            _archive.AddChunk(source, _segmentStart, count);
            _chunks.Add((source.SourceId, _segmentStart, count));
            _segmentStart = _position;
            _writer = null;
        }
        catch { writer.RestoreFinalizedAssetForRecovery(); throw; }
        finally { writer.Dispose(); }
    }
    public IReadOnlyList<CompanionSpan> Spans(long start, long end)
    {
        lock (_gate)
        {
            if (start < 0 || end <= start || end > _position) throw new ArgumentOutOfRangeException(nameof(start));
            return _chunks.Where(c => c.Start < end && c.Start + c.Count > start)
                .Select(c => new CompanionSpan(c.Id, Math.Max(0, start - c.Start), Math.Min(c.Count, end - c.Start))).ToArray();
        }
    }
    public void Dispose()
    {
        if (_input is not null)
        {
            var input = _input; _input = null;
            try { input.Stop(); } finally { input.DataAvailable -= OnData; input.CaptureError -= OnError; input.Dispose(); }
        }
        lock (_gate) { try { FlushSegmentCore(); } finally { _writer?.Dispose(); _writer = null; } }
    }
}
