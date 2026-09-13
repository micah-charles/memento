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
    public async Task Worker_retries_failed_processor_with_bounded_backoff()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-worker"));
        var job = new ConversationSessionWriter(repository).QueueTranscription(session, null, source, DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
        var processor = new FlakyProcessor();
        var worker = new ConversationJobWorker(repository, processor, TimeSpan.FromSeconds(10));

        var first = await worker.RunOnceAsync(DateTimeOffset.Parse("2026-09-13T10:00:01Z"));
        var hidden = await worker.RunOnceAsync(DateTimeOffset.Parse("2026-09-13T10:00:05Z"));
        var second = await worker.RunOnceAsync(DateTimeOffset.Parse("2026-09-13T10:00:12Z"));

        Assert.Equal(1, first.Failed);
        Assert.Equal(0, hidden.Examined);
        Assert.Equal(1, second.Succeeded);
        Assert.Equal(2, processor.Calls);
        Assert.Empty(repository.ListRetryableConversationJobs(DateTimeOffset.Parse("2026-09-13T10:01:00Z")));
    }

    [Fact]
    public async Task Durable_transcription_processor_persists_one_initial_revision_and_is_idempotent()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var sourcePath = Path.Combine(fixture.DirectoryPath, "source.wav");
        File.WriteAllBytes(sourcePath, [1, 2]);
        var source = repository.AddSource(new SourceMetadata("source-transcription", "audio", session.SessionId, null, sourcePath, "PCM WAV", 48000, 1, 16, 2, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var job = new ConversationSessionWriter(repository).QueueTranscription(session, null, source);
        var provider = new FakeTranscriptionProvider();
        var processor = new DurableTranscriptionJobProcessor(repository, provider, "yue");

        await processor.ProcessAsync(job);
        await processor.ProcessAsync(job);

        Assert.Equal(1, provider.Calls);
        var revisions = repository.ListTranscriptRevisions(source.SourceId);
        Assert.Single(revisions);
        Assert.Equal("synthetic transcript", revisions[0].Text);
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

    [Fact]
    public void Response_episode_links_stimulus_response_and_optional_follow_up_evidence()
    {
        using var fixture = new PersistenceFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-episode"));
        var stimulusRevision = repository.AddTranscriptRevision(new TranscriptRevision("revision-stimulus", source.SourceId, null, 1, "initial", "Micah won a music competition", 1, null, DateTimeOffset.UtcNow));
        var responseRevision = repository.AddTranscriptRevision(new TranscriptRevision("revision-response", source.SourceId, null, 2, "initial", "好叻呀，點樣練㗎？", 1, null, DateTimeOffset.UtcNow));
        var stimulus = repository.AddEvidence(new EvidenceRecord("evidence-stimulus", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, null, stimulusRevision.TranscriptRevisionId, stimulusRevision.Text, stimulusRevision.Text, ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
        var response = repository.AddEvidence(new EvidenceRecord("evidence-response", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, null, responseRevision.TranscriptRevisionId, responseRevision.Text, responseRevision.Text, ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
        var episode = repository.AddResponseEpisode(new ResponseEpisode("episode-1", session.SessionId, stimulus.EvidenceId, response.EvidenceId, null, "Participant praised the result and asked a practical follow-up.", "quoted_language", DateTimeOffset.UtcNow));

        ProvenanceGraph.ValidateResponseEpisode(episode, stimulus, response);
        Assert.Equal("quoted_language", episode.ObservationBasis);
    }

    private sealed class PersistenceFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memento-persistence-tests", Guid.NewGuid().ToString("N"));
        public PersistenceFixture() => Directory.CreateDirectory(_directory);
        public string DirectoryPath => _directory;
        public SqliteArchive CreateArchive() { var archive = new SqliteArchive(Path.Combine(_directory, "data", "memory.db")); archive.Initialize(); return archive; }
        public SourceMetadata Source(string sessionId, string id) => new(id, "audio", sessionId, null, "raw/audio.wav", "PCM WAV", 48000, 1, 16, 100, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class FlakyProcessor : IConversationJobProcessor
    {
        public int Calls { get; private set; }
        public Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("simulated offline");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTranscriptionProvider : ITranscriptionProvider
    {
        public int Calls { get; private set; }
        public string Provider => "fake-transcription";
        public string Model => "fake-transcribe-v1";
        public Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal("yue", language);
            return Task.FromResult(new TranscriptionResult(Provider, Model, "fake-request", "synthetic transcript", DateTimeOffset.UtcNow));
        }
    }
}
