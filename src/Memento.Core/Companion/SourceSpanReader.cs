using Memento.Core.Conversation;
using Memento.Core.Storage;

namespace Memento.Core.Companion;

public sealed class SourceSpanReader(ArchiveRepository repository, string audioRoot)
{
    public byte[] Read(CompanionSpan span)
    {
        var source = repository.GetSource(span.SourceId) ?? throw new InvalidDataException("Source missing.");
        if (source.RecoveryStatus == "withdrawn") throw new InvalidOperationException("Source withdrawn.");
        SourcePathGuard.EnsureMatches(source, source.FilePath!, audioRoot);
        if (source.SampleRate != 48000 || source.Channels != 1 || source.BitDepth != 16 || span.StartSample < 0 || span.EndSample <= span.StartSample) throw new InvalidDataException("Invalid source span.");
        using var reader = new NAudio.Wave.WaveFileReader(source.FilePath);
        if (span.EndSample * 2 > reader.Length) throw new InvalidDataException("Span exceeds source.");
        reader.Position = span.StartSample * 2;
        var buffer = new byte[checked((int)((span.EndSample - span.StartSample) * 2))];
        reader.ReadExactly(buffer); return buffer;
    }
}
