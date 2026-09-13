using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Memory;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class PersistenceAndMemoryTests
{
    [Fact]
    public void Conversation_job_survives_failure_and_is_retryable()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-job"));
        var writer = new ConversationSessionWriter(repository);
        var job = writer.QueueTranscription(session, null, source, DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        var processing = writer.BeginAttempt(job, DateTimeOffset.Parse("2026-09-13T10:00:01Z"));
        var failed = writer.MarkFailed(processing, "network unavailable", DateTimeOffset.Parse("2026-09-13T10:01:00Z"), DateTimeOffset.Parse("2026-09-13T10:00:02Z"));

        Assert.Equal(ConversationJobStatus.Failed, failed.Status);
        Assert.Single(repository.ListRetryableConversationJobs(DateTimeOffset.Parse("2026-09-13T10:02:00Z")));
        Assert.Empty(repository.ListRetryableConversationJobs(DateTimeOffset.Parse("2026-09-13T10:00:30Z")));
    }

    [Fact]
    public void Candidate_extraction_keeps_evidence_claim_and_link_separate()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-memory"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-memory", source.SourceId, null, 1, "initial", "我鍾意食魚蛋", 0.93, null, DateTimeOffset.UtcNow));
        var result = new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision);

        var evidence = Assert.Single(result.Evidence);
        var claim = Assert.Single(result.Claims);
        var link = Assert.Single(result.Links);
        Assert.Equal(EvidenceKind.DirectStatement, evidence.Kind);
        Assert.Equal(ClaimStatus.Candidate, claim.Status);
        ProvenanceGraph.Validate(source, revision, evidence, claim, link);
    }

    [Fact]
    public void Provenance_rejects_mismatched_source_or_unreviewed_inference_promotion()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-provenance"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-provenance", source.SourceId, null, 1, "initial", "test", 0.8, null, DateTimeOffset.UtcNow));
        var evidence = repository.AddEvidence(new EvidenceRecord("evidence-provenance", EvidenceKind.AiInference, source.SourceId, session.SessionId, null, revision.TranscriptRevisionId, "hypothesis", "test", ParticipantCertainty.Unknown, false, DateTimeOffset.UtcNow));
        var claim = repository.AddMemoryClaim(new MemoryClaim("claim-provenance", "hypothesis", null, "said", "test", ClaimStatus.Reviewed, DateTimeOffset.UtcNow));
        var link = repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidence.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(() => ProvenanceGraph.Validate(source, revision, evidence, claim, link));
    }

    [Fact]
    public void Entity_alias_and_evidence_link_preserve_speaker_confirmation()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-entity"));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-entity", source.SourceId, null, 1, "initial", "阿貞", 1, null, DateTimeOffset.UtcNow));
        var evidence = repository.AddEvidence(new EvidenceRecord("evidence-entity", EvidenceKind.ConfirmedInterpretation, source.SourceId, session.SessionId, null, revision.TranscriptRevisionId, "阿貞", "阿貞", ParticipantCertainty.Stated, true, DateTimeOffset.UtcNow));
        var resolver = new EntityResolutionService(repository);
        var person = resolver.CreatePerson("阿貞", "childhood friend");
        var alias = resolver.AddSpeakerConfirmedAlias(person, "阿珍");
        var link = resolver.LinkEvidence(evidence, person, "person_mentioned");

        Assert.True(alias.SpeakerConfirmed);
        Assert.Equal("阿珍", alias.Alias);
        Assert.Equal(person.PersonEntityId, link.PersonEntityId);
    }

    private sealed class PersistenceFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memento-persistence-tests", Guid.NewGuid().ToString("N"));
        public PersistenceFixture() => Directory.CreateDirectory(_directory);
        public SqliteArchive CreateArchive() { var archive = new SqliteArchive(Path.Combine(_directory, "data", "memory.db")); archive.Initialize(); return archive; }
        public SourceMetadata Source(string sessionId, string id) => new(id, "audio", sessionId, null, "raw/audio.wav", "PCM WAV", 48000, 1, 16, 100, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
