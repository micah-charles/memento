using System.Security.Cryptography;
using Memento.Core.Admin;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class DeletionTests
{
    [Fact]
    public void Deletion_requires_family_admin_authorization()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-auth"));
        var service = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"));

        Assert.Throws<UnauthorizedAccessException>(() => service.DeleteSource("wrong", source.SourceId, "participant request"));
        Assert.NotNull(repository.GetSource(source.SourceId));
    }

    [Fact]
    public void Authorized_source_deletion_removes_dependents_media_and_keeps_minimal_tombstone()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var turn = repository.AddTurn(session.SessionId, 0, "participant", DateTimeOffset.UtcNow, turnId: "turn-delete");
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-delete", turn.TurnId));
        var first = repository.AddTranscriptRevision(new TranscriptRevision("revision-delete-1", source.SourceId, turn.TurnId, 1, "initial", "阿珍", 0.8, null, DateTimeOffset.UtcNow));
        var corrected = repository.AddTranscriptRevision(new TranscriptRevision("revision-delete-2", source.SourceId, turn.TurnId, 2, "corrected", "阿貞", 1, first.TranscriptRevisionId, DateTimeOffset.UtcNow));
        repository.AddClarificationEvent(new ClarificationEvent("clarification-delete", session.SessionId, turn.TurnId, source.SourceId, "PersonName", "係咪阿珍？", first.TranscriptRevisionId, "係阿貞", corrected.TranscriptRevisionId, ClarificationOutcome.SpeakerConfirmed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        repository.AddVocabularyEntry(new VocabularyEntry("vocabulary-delete", "阿貞", "阿珍", "childhood friend", true, "clarification-delete", DateTimeOffset.UtcNow));
        repository.AddConversationJob(new ConversationJob("job-delete", session.SessionId, turn.TurnId, source.SourceId, "durable_extraction", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, corrected.TranscriptRevisionId));
        repository.AddProviderInteraction(new ProviderInteraction("interaction-delete", session.SessionId, turn.TurnId, "test", "transcription", "test-v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 100, null, true, null, null, DateTimeOffset.UtcNow));
        var derivedBytes = File.ReadAllBytes(fixture.DerivedPath);
        repository.AddDerivedSpeechAsset(new DerivedSpeechAsset("derived-delete", session.SessionId, turn.TurnId, fixture.DerivedPath, "wav", derivedBytes.LongLength, Convert.ToHexString(SHA256.HashData(derivedBytes)).ToLowerInvariant(), "test", "test-v1", "test", null, DateTimeOffset.UtcNow));

        var person = repository.AddPersonEntity(new PersonEntity("person-delete", "阿貞", "friend", DateTimeOffset.UtcNow));
        repository.AddEntityAlias(new EntityAlias("alias-delete", person.PersonEntityId, "阿珍", true, "clarification-delete", DateTimeOffset.UtcNow));
        var evidenceOne = repository.AddEvidence(new EvidenceRecord("evidence-delete-1", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, turn.TurnId, first.TranscriptRevisionId, "阿珍", "阿珍", ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
        var evidenceTwo = repository.AddEvidence(new EvidenceRecord("evidence-delete-2", EvidenceKind.ConfirmedInterpretation, source.SourceId, session.SessionId, turn.TurnId, corrected.TranscriptRevisionId, "阿貞", "阿貞", ParticipantCertainty.Stated, true, DateTimeOffset.UtcNow));
        repository.AddEvidenceEntityLink(new EvidenceEntityLink(evidenceTwo.EvidenceId, person.PersonEntityId, "subject", DateTimeOffset.UtcNow));
        var claim = repository.AddMemoryClaim(new MemoryClaim("claim-delete", "Participant knows 阿貞", person.PersonEntityId, "knows", "阿貞", ClaimStatus.Candidate, DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidenceOne.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
        var sourceOnlyClaim = repository.AddMemoryClaim(new MemoryClaim("claim-delete-source-only", "Only this Source supports me", null, "supports", "this Source", ClaimStatus.Candidate, DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidenceOne.EvidenceId, sourceOnlyClaim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
        var otherSource = repository.AddSource(fixture.OtherSource(session.SessionId, "source-other"));
        var otherEvidence = repository.AddEvidence(new EvidenceRecord("evidence-other", EvidenceKind.DirectStatement, otherSource.SourceId, session.SessionId, null, null, "other source", "other source", ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(otherEvidence.EvidenceId, claim.MemoryClaimId, "contextualises", DateTimeOffset.UtcNow));
        repository.AddResponseEpisode(new ResponseEpisode("episode-delete", session.SessionId, evidenceOne.EvidenceId, evidenceTwo.EvidenceId, null, "correction", "direct_response", DateTimeOffset.UtcNow));
        repository.AddReviewAnnotation(new ReviewAnnotation("annotation-delete", "memory_claim", claim.MemoryClaimId, "admin-1", "family_assessment", "reviewed", "supported", DateTimeOffset.UtcNow));

        var service = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"));
        var result = service.DeleteSource("admin-1", source.SourceId, "participant requested deletion");

        Assert.True(result.MediaRemoved);
        Assert.Empty(result.Findings);
        Assert.False(File.Exists(source.FilePath));
        Assert.False(File.Exists(fixture.DerivedPath));
        Assert.Null(repository.GetSource(source.SourceId));
        Assert.NotNull(repository.GetSource(otherSource.SourceId));
        using var connection = archive.OpenConnection();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM transcript_revisions WHERE source_id = 'source-delete'"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM evidence_records WHERE source_id = 'source-delete'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM memory_claims WHERE memory_claim_id = 'claim-delete'"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM memory_claims WHERE memory_claim_id = 'claim-delete-source-only'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM evidence_records WHERE evidence_id = 'evidence-other'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM evidence_claim_links"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM review_annotations WHERE target_id = 'claim-delete'"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM person_entities WHERE person_entity_id = 'person-delete'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM deletion_tombstones WHERE target_id = 'source-delete' AND media_removed = 1"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM deletion_tombstones WHERE deletion_tombstone_id = '" + result.TombstoneId + "'"));
        Assert.True(archive.IsIntegrityCheckClean());
        Assert.Empty(new ArchiveSearchService(archive).Search("阿珍"));
        Assert.NotEmpty(new ArchiveSearchService(archive).Search("Participant knows"));
    }

    [Fact]
    public void Session_level_source_deletion_removes_unambiguous_session_assets()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-session-level"));
        repository.AddProviderInteraction(new ProviderInteraction("interaction-session-level", session.SessionId, null, "test", "response", "test-v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, true, null, null, DateTimeOffset.UtcNow));
        var derivedBytes = File.ReadAllBytes(fixture.DerivedPath);
        repository.AddDerivedSpeechAsset(new DerivedSpeechAsset("derived-session-level", session.SessionId, null, fixture.DerivedPath, "wav", derivedBytes.LongLength, Convert.ToHexString(SHA256.HashData(derivedBytes)).ToLowerInvariant(), "test", "test-v1", "test", null, DateTimeOffset.UtcNow));

        var result = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"))
            .DeleteSource("admin-1", source.SourceId, "participant requested deletion");

        Assert.True(result.MediaRemoved);
        Assert.Empty(result.Findings);
        Assert.False(File.Exists(source.FilePath));
        Assert.False(File.Exists(fixture.DerivedPath));
        using var connection = archive.OpenConnection();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM provider_interactions WHERE session_id = '" + session.SessionId + "'"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM derived_speech_assets WHERE session_id = '" + session.SessionId + "'"));
    }

    [Fact]
    public void Source_deletion_retains_media_still_referenced_by_another_record()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-shared-delete"));
        var remaining = repository.AddSource(fixture.Source(session.SessionId, "source-shared-remaining"));

        var result = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"))
            .DeleteSource("admin-1", source.SourceId, "remove one duplicate reference");

        Assert.False(result.MediaRemoved);
        Assert.Contains(result.Findings, finding => finding.Contains("another archive record", StringComparison.Ordinal));
        Assert.Null(repository.GetSource(source.SourceId));
        Assert.NotNull(repository.GetSource(remaining.SourceId));
        Assert.True(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public void Session_level_source_deletion_retains_ambiguous_session_assets_with_finding()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-session-level-first"));
        var otherSource = repository.AddSource(fixture.OtherSource(session.SessionId, "source-session-level-second"));
        repository.AddProviderInteraction(new ProviderInteraction("interaction-ambiguous", session.SessionId, null, "test", "response", "test-v1", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, true, null, null, DateTimeOffset.UtcNow));
        var derivedBytes = File.ReadAllBytes(fixture.DerivedPath);
        repository.AddDerivedSpeechAsset(new DerivedSpeechAsset("derived-ambiguous", session.SessionId, null, fixture.DerivedPath, "wav", derivedBytes.LongLength, Convert.ToHexString(SHA256.HashData(derivedBytes)).ToLowerInvariant(), "test", "test-v1", "test", null, DateTimeOffset.UtcNow));

        var result = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"))
            .DeleteSource("admin-1", source.SourceId, "participant requested deletion");

        Assert.True(result.MediaRemoved);
        Assert.Contains(result.Findings, finding => finding.Contains("Session-level provider metadata", StringComparison.Ordinal));
        Assert.NotNull(repository.GetSource(otherSource.SourceId));
        Assert.True(File.Exists(fixture.DerivedPath));
        using var connection = archive.OpenConnection();
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM provider_interactions WHERE provider_interaction_id = 'interaction-ambiguous'"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM derived_speech_assets WHERE derived_speech_asset_id = 'derived-ambiguous'"));
    }

    [Fact]
    public void Sessionless_placeholder_source_can_be_deleted_without_crashing()
    {
        using var fixture = new DeletionFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var source = repository.AddSource(new SourceMetadata("source-placeholder", "audio", null, null, fixture.SourcePath, "PCM WAV", 48000, 1, 16, 3, 0, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.SourcePath))).ToLowerInvariant(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));

        var result = new ArchiveDeletionService(repository, new FixedTestAdminAuthorizer("admin-1"))
            .DeleteSource("admin-1", source.SourceId, "remove placeholder");

        Assert.True(result.MediaRemoved);
        Assert.Contains(result.Findings, finding => finding.Contains("has no session", StringComparison.Ordinal));
        Assert.Null(repository.GetSource(source.SourceId));
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class DeletionFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memento-deletion-tests", Guid.NewGuid().ToString("N"));
        public DeletionFixture()
        {
            Directory.CreateDirectory(_directory);
            SourcePath = Path.Combine(_directory, "recording.wav");
            File.WriteAllBytes(SourcePath, [1, 2, 3]);
            OtherPath = Path.Combine(_directory, "other.wav");
            File.WriteAllBytes(OtherPath, [7, 8, 9]);
            DerivedPath = Path.Combine(_directory, "derived.wav");
            File.WriteAllBytes(DerivedPath, [4, 5, 6]);
        }

        public string SourcePath { get; }
        public string OtherPath { get; }
        public string DerivedPath { get; }
        public SourceMetadata Source(string sessionId, string id, string? turnId = null)
            => new(id, "audio", sessionId, turnId, SourcePath, "PCM WAV", 48000, 1, 16, 3, 0, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SourcePath))).ToLowerInvariant(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public SourceMetadata OtherSource(string sessionId, string id)
            => new(id, "audio", sessionId, null, OtherPath, "PCM WAV", 48000, 1, 16, 3, 0, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(OtherPath))).ToLowerInvariant(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public SqliteArchive CreateArchive()
        {
            var archive = new SqliteArchive(Path.Combine(_directory, "data", "memory.db"));
            archive.Initialize();
            return archive;
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
