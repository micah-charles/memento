using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Memento.Core.Admin;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Memory;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class WithdrawalTests
{
    [Fact]
    public async Task Authorized_withdrawal_retains_history_but_blocks_future_cloud_use_search_and_default_exports()
    {
        using var fixture = new WithdrawalFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(fixture.Source(session.SessionId));
        var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-withdrawal", source.SourceId, null, 1, "initial", "我鍾意食魚蛋", 0.9, null, DateTimeOffset.UtcNow));
        var evidence = repository.AddEvidence(new EvidenceRecord("evidence-withdrawal", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, null, revision.TranscriptRevisionId, revision.Text, revision.Text, ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
        var claim = repository.AddMemoryClaim(new MemoryClaim("claim-withdrawal", "Participant likes fish balls", null, "likes", "fish balls", ClaimStatus.Candidate, DateTimeOffset.UtcNow));
        repository.AddEvidenceClaimLink(new EvidenceClaimLink(evidence.EvidenceId, claim.MemoryClaimId, "supports", DateTimeOffset.UtcNow));
        repository.AddConversationJob(new ConversationJob("job-withdrawal", session.SessionId, null, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var service = new ArchiveWithdrawalService(repository, new FixedTestAdminAuthorizer("admin-1"));
        Assert.Throws<UnauthorizedAccessException>(() => service.WithdrawSource("wrong", source.SourceId, "participant request"));

        var result = service.WithdrawSource("admin-1", source.SourceId, "participant request");

        Assert.True(result.Changed);
        Assert.Equal(1, result.BlockedJobCount);
        Assert.Equal("withdrawn", repository.GetSource(source.SourceId)!.RecoveryStatus);
        Assert.True(File.Exists(source.FilePath));
        using (var connection = archive.OpenConnection())
        {
            using var annotation = connection.CreateCommand();
            annotation.CommandText = "SELECT COUNT(*) FROM review_annotations WHERE target_type = 'source' AND target_id = $source AND annotation_type = 'withdrawal'";
            annotation.Parameters.AddWithValue("$source", source.SourceId);
            Assert.Equal(1L, Convert.ToInt64(annotation.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var search = new ArchiveSearchService(archive);
        Assert.Empty(search.Search("魚蛋"));
        Assert.Empty(search.Search("fish balls"));
        Assert.Empty(new FamilyAdminReviewService(repository, new FixedTestAdminAuthorizer("admin-1")).ListCandidates("admin-1"));
        var writer = new ConversationSessionWriter(repository);
        Assert.Throws<CloudNotPermittedException>(() => writer.QueueTranscription(session, null, source));
        Assert.Null(writer.QueueExtractionIfNeeded(session.SessionId, null, source.SourceId, revision.TranscriptRevisionId));
        Assert.Throws<CloudNotPermittedException>(() => new MemoryExtractionService(repository, new DeterministicMemoryExtractionProvider()).ExtractAndPersist(session, source, revision));
        await Assert.ThrowsAsync<CloudNotPermittedException>(() => new AsyncMemoryExtractionService(repository, new FailingExtractionProvider()).ExtractAndPersistAsync(session, source, revision));
        using (var jobConnection = archive.OpenConnection())
        using (var jobCommand = jobConnection.CreateCommand())
        {
            jobCommand.CommandText = "SELECT status FROM conversation_jobs WHERE conversation_job_id = 'job-withdrawal'";
            Assert.Equal("Failed", jobCommand.ExecuteScalar()?.ToString());
        }
        var transcriptionJob = new ConversationJob("transcription-withdrawal", session.SessionId, null, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<CloudNotPermittedException>(() => new DurableTranscriptionJobProcessor(repository, new FailingTranscriptionProvider()).ProcessAsync(transcriptionJob));
        var extractionJob = new ConversationJob("extraction-withdrawal", session.SessionId, null, source.SourceId, "durable_extraction", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, revision.TranscriptRevisionId);
        await Assert.ThrowsAsync<CloudNotPermittedException>(() => new DurableMemoryExtractionJobProcessor(repository, new FailingExtractionProvider()).ProcessAsync(extractionJob));
        var responseJob = new ConversationJob("response-withdrawal", session.SessionId, null, source.SourceId, "durable_response", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<CloudNotPermittedException>(() => new DurableResponseJobProcessor(repository, new DeterministicConversationProvider()).ProcessAsync(responseJob));

        var filtered = ArchiveExporter.Export(archive, fixture.ExportRoot);
        Assert.DoesNotContain(source.SourceId, File.ReadAllText(Path.Combine(filtered.ExportDirectory, "sources.jsonl")), StringComparison.Ordinal);
        Assert.DoesNotContain(claim.MemoryClaimId, File.ReadAllText(Path.Combine(filtered.ExportDirectory, "memory_claims.jsonl")), StringComparison.Ordinal);
        Assert.DoesNotContain(claim.MemoryClaimId, File.ReadAllText(Path.Combine(filtered.ExportDirectory, "evidence_claim_links.jsonl")), StringComparison.Ordinal);
        Assert.Contains(result.AnnotationId, File.ReadAllText(Path.Combine(filtered.ExportDirectory, "review_annotations.jsonl")), StringComparison.Ordinal);
        using (var filteredConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(filtered.ExportDirectory, "archive.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            filteredConnection.Open();
            using var count = filteredConnection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sources WHERE source_id = $source";
            count.Parameters.AddWithValue("$source", source.SourceId);
            Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var complete = ArchiveExporter.Export(archive, fixture.ExportRoot, includeWithdrawn: true);
        Assert.Contains(source.SourceId, File.ReadAllText(Path.Combine(complete.ExportDirectory, "sources.jsonl")), StringComparison.Ordinal);
        Assert.Contains(claim.MemoryClaimId, File.ReadAllText(Path.Combine(complete.ExportDirectory, "memory_claims.jsonl")), StringComparison.Ordinal);
    }

    private sealed class WithdrawalFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memento-withdrawal-tests", Guid.NewGuid().ToString("N"));

        public WithdrawalFixture()
        {
            Directory.CreateDirectory(_directory);
            AudioPath = Path.Combine(_directory, "recording.wav");
            File.WriteAllBytes(AudioPath, [1, 2, 3]);
            ExportRoot = Path.Combine(_directory, "exports");
        }

        public string AudioPath { get; }
        public string ExportRoot { get; }
        public SqliteArchive CreateArchive()
        {
            var archive = new SqliteArchive(Path.Combine(_directory, "data", "memory.db"));
            archive.Initialize();
            return archive;
        }

        public SourceMetadata Source(string sessionId)
            => new("source-withdrawal", "audio", sessionId, null, AudioPath, "PCM WAV", 48000, 1, 16, 3, 0, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AudioPath))).ToLowerInvariant(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class FailingTranscriptionProvider : ITranscriptionProvider
    {
        public string Provider => "withdrawal-test";
        public string Model => "withdrawal-test-v1";
        public Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The withdrawn source must not reach the transcription provider.");
    }

    private sealed class FailingExtractionProvider : Memento.Core.Memory.IAsyncMemoryExtractionProvider
    {
        public string Provider => "withdrawal-test";
        public string Model => "withdrawal-test-v1";
        public Task<IReadOnlyList<Memento.Core.Memory.ExtractionCandidate>> ExtractAsync(TranscriptRevision revision, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The withdrawn source must not reach the extraction provider.");
    }
}
