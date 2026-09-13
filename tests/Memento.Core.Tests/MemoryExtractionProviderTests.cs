using System.Net;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Memory;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class MemoryExtractionProviderTests
{
    [Fact]
    public async Task OpenAi_memory_extraction_requests_structured_candidates_without_storage()
    {
        var handler = new ExtractionHandler("""
            {
              "output_text": "{\"candidates\":[{\"statement\":\"我鍾意食魚蛋\",\"predicate\":\"likes\",\"object\":\"魚蛋\",\"certainty\":\"Stated\",\"evidence_kind\":\"DirectStatement\",\"subject_person_id\":null}]}"
            }
            """);
        using var http = new HttpClient(handler);
        var provider = new OpenAiMemoryExtractionProvider(http, new FixedCredentialProvider(), "gpt-5.6-terra");
        var revision = new TranscriptRevision("revision-extract", "source-extract", null, 1, "initial", "我鍾意食魚蛋", 0.9, null, DateTimeOffset.UtcNow);

        var candidates = await provider.ExtractAsync(revision);

        var candidate = Assert.Single(candidates);
        Assert.Equal("我鍾意食魚蛋", candidate.Statement);
        Assert.Equal("likes", candidate.Predicate);
        Assert.Equal(EvidenceKind.DirectStatement, candidate.EvidenceKind);
        Assert.Contains("\"store\":false", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"json_schema\"", handler.Body, StringComparison.Ordinal);
        using var requestDocument = System.Text.Json.JsonDocument.Parse(handler.Body);
        Assert.Equal("我鍾意食魚蛋", requestDocument.RootElement.GetProperty("input").GetString());
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Fact]
    public async Task Async_extraction_service_persists_candidates_as_unreviewed_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "memento-extraction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = new SqliteArchive(Path.Combine(directory, "data", "memory.db"));
            archive.Initialize();
            var repository = new ArchiveRepository(archive);
            var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
            var source = repository.AddSource(new SourceMetadata("source-async-extract", "audio", session.SessionId, null, "audio.wav", "PCM WAV", 48000, 1, 16, 4, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
            var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-async-extract", source.SourceId, null, 1, "initial", "我鍾意食魚蛋", 0.9, null, DateTimeOffset.UtcNow));
            var provider = new InlineExtractionProvider();

            var result = await new AsyncMemoryExtractionService(repository, provider).ExtractAndPersistAsync(session, source, revision);

            var evidence = Assert.Single(result.Evidence);
            var claim = Assert.Single(result.Claims);
            Assert.Equal(ClaimStatus.Candidate, claim.Status);
            Assert.Equal(provider.Provider, evidence.ExtractionProvider);
            Assert.Equal(provider.Model, evidence.ExtractionModel);
            Assert.Single(result.Links);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Durable_extraction_processor_rechecks_consent_and_persists_candidates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "memento-durable-extraction-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = new SqliteArchive(Path.Combine(directory, "data", "memory.db"));
            archive.Initialize();
            var repository = new ArchiveRepository(archive);
            var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
            repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
            var source = repository.AddSource(new SourceMetadata("source-durable-extract", "audio", session.SessionId, null, "audio.wav", "PCM WAV", 48000, 1, 16, 4, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
            var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-durable-extract", source.SourceId, null, 1, "initial", "我鍾意食魚蛋", 0.9, null, DateTimeOffset.UtcNow));
            repository.AddTranscriptRevision(new TranscriptRevision("revision-durable-extract-corrected", source.SourceId, null, 2, "corrected", "我鍾意食雞蛋", 0.9, revision.TranscriptRevisionId, DateTimeOffset.UtcNow));
            var job = new ConversationJob("job-durable-extract", session.SessionId, null, source.SourceId, "durable_extraction", ConversationJobStatus.Pending, 0, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, revision.TranscriptRevisionId);
            var result = new DurableMemoryExtractionJobProcessor(repository, new InlineExtractionProvider());

            await result.ProcessAsync(job);

            var claim = Assert.Single(repository.ListCandidateClaims());
            Assert.Equal(revision.Text, claim.Statement);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FixedCredentialProvider : IApiCredentialProvider
    {
        public string? GetApiKey() => "test-key";
    }

    private sealed class InlineExtractionProvider : IAsyncMemoryExtractionProvider
    {
        public string Provider => "inline-extraction";
        public string Model => "inline-extraction-v1";
        public Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(TranscriptRevision revision, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExtractionCandidate>>([new ExtractionCandidate(revision.Text, "said", revision.Text, ParticipantCertainty.Stated)]);
    }

    private sealed class ExtractionHandler(string responseBody) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
