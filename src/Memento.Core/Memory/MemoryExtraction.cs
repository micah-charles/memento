using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Memory;

public sealed record ExtractionCandidate(
    string Statement,
    string Predicate,
    string Object,
    ParticipantCertainty Certainty,
    EvidenceKind EvidenceKind = EvidenceKind.DirectStatement,
    string? SubjectPersonId = null);

public interface IMemoryExtractionProvider
{
    string Provider { get; }
    string Model { get; }
    IReadOnlyList<ExtractionCandidate> Extract(TranscriptRevision revision);
}

public sealed class DeterministicMemoryExtractionProvider : IMemoryExtractionProvider
{
    public string Provider => "deterministic-test";
    public string Model => "candidate-extractor-v1";

    public IReadOnlyList<ExtractionCandidate> Extract(TranscriptRevision revision)
    {
        if (string.IsNullOrWhiteSpace(revision.Text)) return [];
        return [new ExtractionCandidate(revision.Text, "said", revision.Text, ParticipantCertainty.Stated)];
    }
}

public sealed record ExtractionResult(IReadOnlyList<EvidenceRecord> Evidence, IReadOnlyList<MemoryClaim> Claims, IReadOnlyList<EvidenceClaimLink> Links);

public sealed class MemoryExtractionService
{
    private readonly ArchiveRepository _repository;
    private readonly IMemoryExtractionProvider _provider;

    public MemoryExtractionService(ArchiveRepository repository, IMemoryExtractionProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public ExtractionResult ExtractAndPersist(Session session, SourceMetadata source, TranscriptRevision revision)
    {
        if (revision.SourceId != source.SourceId) throw new InvalidOperationException("Transcript revision and Source do not match.");
        if (source.SessionId != session.SessionId) throw new InvalidOperationException("Source and session do not match.");
        var evidence = new List<EvidenceRecord>();
        var claims = new List<MemoryClaim>();
        var links = new List<EvidenceClaimLink>();
        foreach (var candidate in _provider.Extract(revision))
        {
            if (string.IsNullOrWhiteSpace(candidate.Statement) || string.IsNullOrWhiteSpace(candidate.Predicate) || string.IsNullOrWhiteSpace(candidate.Object))
                continue;
            var now = DateTimeOffset.UtcNow;
            var item = _repository.AddEvidence(new EvidenceRecord(Guid.NewGuid().ToString("N"), candidate.EvidenceKind, source.SourceId, session.SessionId, revision.TurnId, revision.TranscriptRevisionId, candidate.Statement, candidate.Statement, candidate.Certainty, false, now));
            var claim = _repository.AddMemoryClaim(new MemoryClaim(Guid.NewGuid().ToString("N"), candidate.Statement, candidate.SubjectPersonId, candidate.Predicate, candidate.Object, ClaimStatus.Candidate, now));
            var link = _repository.AddEvidenceClaimLink(new EvidenceClaimLink(item.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
            evidence.Add(item); claims.Add(claim); links.Add(link);
        }

        return new ExtractionResult(evidence, claims, links);
    }
}
