using Memento.Core.Conversation;
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

public interface IAsyncMemoryExtractionProvider
{
    string Provider { get; }
    string Model { get; }
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(TranscriptRevision revision, CancellationToken cancellationToken = default);
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
        EnsureSourceAvailable(source);
        return MemoryExtractionPersistence.Persist(_repository, _provider.Provider, _provider.Model, session, source, revision, _provider.Extract(revision));
    }

    private void EnsureSourceAvailable(SourceMetadata source)
    {
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) || string.Equals(_repository.GetSource(source.SourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }
}

public sealed class AsyncMemoryExtractionService
{
    private readonly ArchiveRepository _repository;
    private readonly IAsyncMemoryExtractionProvider _provider;

    public AsyncMemoryExtractionService(ArchiveRepository repository, IAsyncMemoryExtractionProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<ExtractionResult> ExtractAndPersistAsync(Session session, SourceMetadata source, TranscriptRevision revision, CancellationToken cancellationToken = default)
    {
        if (revision.SourceId != source.SourceId) throw new InvalidOperationException("Transcript revision and Source do not match.");
        if (source.SessionId != session.SessionId) throw new InvalidOperationException("Source and session do not match.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) || string.Equals(_repository.GetSource(source.SourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        var candidates = await _provider.ExtractAsync(revision, cancellationToken).ConfigureAwait(false);
        return MemoryExtractionPersistence.Persist(_repository, _provider.Provider, _provider.Model, session, source, revision, candidates);
    }
}

internal static class MemoryExtractionPersistence
{
    public static ExtractionResult Persist(ArchiveRepository repository, string provider, string model, Session session, SourceMetadata source, TranscriptRevision revision, IEnumerable<ExtractionCandidate> candidates)
    {
        var evidence = new List<EvidenceRecord>();
        var claims = new List<MemoryClaim>();
        var links = new List<EvidenceClaimLink>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Statement) || string.IsNullOrWhiteSpace(candidate.Predicate) || string.IsNullOrWhiteSpace(candidate.Object))
                continue;
            var now = DateTimeOffset.UtcNow;
            var item = repository.AddEvidence(new EvidenceRecord(Guid.NewGuid().ToString("N"), candidate.EvidenceKind, source.SourceId, session.SessionId, revision.TurnId, revision.TranscriptRevisionId, candidate.Statement, candidate.Statement, candidate.Certainty, false, now, ExtractionProvider: provider, ExtractionModel: model));
            var claim = repository.AddMemoryClaim(new MemoryClaim(Guid.NewGuid().ToString("N"), candidate.Statement, candidate.SubjectPersonId, candidate.Predicate, candidate.Object, ClaimStatus.Candidate, now));
            var link = repository.AddEvidenceClaimLink(new EvidenceClaimLink(item.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
            evidence.Add(item); claims.Add(claim); links.Add(link);
        }

        return new ExtractionResult(evidence, claims, links);
    }
}
