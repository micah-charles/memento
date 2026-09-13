using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ConversationTests
{
    [Fact]
    public async Task Local_source_is_required_before_deterministic_provider_call()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var orchestrator = new ConversationOrchestrator(repository, new DeterministicConversationProvider());

        await Assert.ThrowsAsync<FileNotFoundException>(() => orchestrator.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.MissingPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Finalized_local_source_can_be_sent_and_provider_metadata_is_stored()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [1, 2, 3]);
        var orchestrator = new ConversationOrchestrator(repository, new DeterministicConversationProvider());

        var result = await orchestrator.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow));

        Assert.True(result.CloudAttempted);
        Assert.NotNull(result.Response);
        Assert.Equal("deterministic-test", result.Response!.Provider);
        Assert.True(result.Interaction!.Succeeded);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_interactions WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task Local_capture_only_never_calls_provider()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var provider = new CountingProvider();
        var orchestrator = new ConversationOrchestrator(repository, provider);

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => orchestrator.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.LocalCaptureOnly, true, DateTimeOffset.UtcNow)));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Cloud_consent_is_required_even_when_audio_exists()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [1]);
        var provider = new CountingProvider();
        var orchestrator = new ConversationOrchestrator(repository, provider);

        await Assert.ThrowsAsync<CloudConsentRequiredException>(() => orchestrator.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, false, DateTimeOffset.UtcNow)));

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task OpenAi_transcription_adapter_sends_local_wav_without_logging_content()
    {
        using var fixture = new ConversationFixture();
        File.WriteAllBytes(fixture.AudioPath, [1, 2, 3]);
        var handler = new RecordingHttpHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var provider = new OpenAiTranscriptionProvider(http, new DelegateApiCredentialProvider(() => "test-key"));

        var result = await provider.TranscribeAsync(fixture.AudioPath, "yue");

        Assert.Equal("阿貞", result.Text);
        Assert.Equal("openai", result.Provider);
        Assert.Equal("req-test", result.RequestId);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("v1/audio/transcriptions", handler.RequestUri!.AbsolutePath.TrimStart('/'));
        Assert.Contains("Bearer test-key", handler.Authorization);
        Assert.Contains("gpt-transcribe", handler.Body);
        Assert.Contains("yue", handler.Body);
        Assert.DoesNotContain("阿貞", handler.Authorization);
    }

    [Fact]
    public async Task OpenAi_responses_adapter_sends_transcript_with_store_disabled()
    {
        using var fixture = new ConversationFixture();
        File.WriteAllBytes(fixture.AudioPath, [1]);
        var handler = new ResponseHttpHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiResponsesProvider(http, new DelegateApiCredentialProvider(() => "test-key"), "gpt-test");
        var request = new ConversationRequest("session", null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow, "我今日去飲茶");

        var result = await provider.SendAsync(request);

        Assert.Equal("回覆內容", result.Text);
        Assert.Contains("input_text", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"store\":false", handler.Body, StringComparison.Ordinal);
        Assert.Equal("v1/responses", handler.RequestUri!.AbsolutePath.Trim('/'));
    }

    [Fact]
    public async Task Bounded_pipeline_persists_transcription_then_response_metadata()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [1, 2]);
        var source = repository.AddSource(new SourceMetadata("source-pipeline", "audio", session.SessionId, null, fixture.AudioPath, "PCM WAV", 48000, 1, 16, 2, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var service = new BoundedVoiceConversationService(repository, new InlineTranscriptionProvider(), new DeterministicConversationProvider(), new DeterministicSpeechOutputProvider(), queueExtractionJobs: true);

        var result = await service.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow, SourceId: source.SourceId));

        Assert.Equal("synthetic yue transcript", result.Transcription.Text);
        Assert.NotNull(result.Conversation.Response);
        Assert.NotNull(result.SpeechOutput);
        Assert.Equal("wav", result.SpeechOutput!.Format);
        Assert.Single(repository.ListTranscriptRevisions(source.SourceId));
        Assert.Contains(repository.ListRetryableConversationJobs(DateTimeOffset.UtcNow.AddMinutes(1)), job => job.JobType == "durable_extraction");
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_interactions WHERE session_id = $session AND succeeded = 1";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(3L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task Derived_speech_is_atomically_stored_and_verified_separately_from_source_audio()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [1, 2]);
        var source = repository.AddSource(new SourceMetadata("source-derived", "audio", session.SessionId, null, fixture.AudioPath, "PCM WAV", 48000, 1, 16, 2, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var store = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));
        var playback = new RecordingSpeechPlayback();
        var service = new BoundedVoiceConversationService(repository, new InlineTranscriptionProvider(), new DeterministicConversationProvider(), new DeterministicSpeechOutputProvider(), store, playback);

        var result = await service.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow, SourceId: source.SourceId));

        Assert.NotNull(result.SpeechAsset);
        var asset = result.SpeechAsset!;
        Assert.True(File.Exists(asset.FilePath));
        Assert.Equal(result.SpeechOutput!.AudioBytes, store.ReadVerified(asset));
        Assert.Same(asset, playback.Asset);
        Assert.Single(repository.ListDerivedSpeechAssets(session.SessionId));
        Assert.Equal(asset.DerivedSpeechAssetId, repository.GetLatestDerivedSpeechAsset()!.DerivedSpeechAssetId);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_interactions WHERE session_id = $session AND capability = 'speech_output' AND succeeded = 1";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));

        File.AppendAllBytes(asset.FilePath, [99]);
        Assert.Throws<InvalidDataException>(() => store.ReadVerified(asset));
    }

    [Fact]
    public async Task Retryable_transcription_failure_queues_a_durable_job_without_touching_source()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [1, 2]);
        var source = repository.AddSource(new SourceMetadata("source-retry", "audio", session.SessionId, null, fixture.AudioPath, "PCM WAV", 48000, 1, 16, 2, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var service = new BoundedVoiceConversationService(repository, new FailingTranscriptionProvider(), new DeterministicConversationProvider());

        await Assert.ThrowsAsync<ProviderRequestException>(() => service.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow, SourceId: source.SourceId)));

        var job = Assert.Single(repository.ListRetryableConversationJobs(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal("durable_transcription", job.JobType);
        Assert.Equal(source.SourceId, job.SourceId);
        Assert.True(File.Exists(fixture.AudioPath));
    }

    [Fact]
    public async Task Retryable_response_failure_queues_a_durable_response_job_after_transcript_persistence()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        File.WriteAllBytes(fixture.AudioPath, [1, 2]);
        var source = repository.AddSource(new SourceMetadata("source-response-retry", "audio", session.SessionId, null, fixture.AudioPath, "PCM WAV", 48000, 1, 16, 2, 0, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var service = new BoundedVoiceConversationService(repository, new InlineTranscriptionProvider(), new FailingResponseProvider());

        var result = await service.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow, SourceId: source.SourceId));

        Assert.Null(result.Conversation.Response);
        var job = Assert.Single(repository.ListRetryableConversationJobs(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal("durable_response", job.JobType);
        Assert.Single(repository.ListTranscriptRevisions(source.SourceId));
    }

    [Fact]
    public async Task OpenAi_speech_adapter_returns_derived_audio_bytes_without_archive_side_effects()
    {
        var handler = new SpeechHttpHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiSpeechOutputProvider(http, new DelegateApiCredentialProvider(() => "test-key"), "tts-test", "alloy");

        var result = await provider.SynthesizeAsync("你好");

        Assert.Equal("openai", result.Provider);
        Assert.Equal("tts-test", result.Model);
        Assert.Equal("wav", result.Format);
        Assert.Equal([1, 2, 3], result.AudioBytes);
        Assert.Equal("v1/audio/speech", handler.RequestUri!.AbsolutePath.Trim('/'));
        Assert.Contains("alloy", handler.Body);
        Assert.Contains("input", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_is_recorded_without_removing_local_audio()
    {
        using var fixture = new ConversationFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        File.WriteAllBytes(fixture.AudioPath, [4, 5, 6]);
        var orchestrator = new ConversationOrchestrator(repository, new FailingProvider());

        var result = await orchestrator.ExecuteAsync(new ConversationRequest(session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow));

        Assert.Null(result.Response);
        Assert.False(result.Interaction!.Succeeded);
        Assert.True(File.Exists(fixture.AudioPath));
        Assert.Equal("ProviderUnavailableException", result.Interaction.ErrorCode);
    }

    private sealed class CountingProvider : IConversationProvider
    {
        public int Calls { get; private set; }
        public string Provider => "counting-test";
        public string Model => "counting-v1";
        public Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ConversationResponse(Provider, "conversation", Model, null, "request", "ok", null, null, DateTimeOffset.UtcNow));
        }
    }

    private sealed class FailingProvider : IConversationProvider
    {
        public string Provider => "failing-test";
        public string Model => "failing-v1";
        public Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default)
            => throw new ProviderUnavailableException();
    }

    private sealed class FailingResponseProvider : IConversationProvider
    {
        public string Provider => "failing-response";
        public string Model => "failing-response-v1";
        public Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default)
            => throw new ProviderRequestException("simulated response network failure", 503);
    }

    private sealed class ProviderUnavailableException() : InvalidOperationException;

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string Authorization { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Headers = { { "x-request-id", "req-test" } },
                Content = new StringContent("{\"text\":\"阿貞\"}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ResponseHttpHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output_text\":\"回覆內容\"}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SpeechHttpHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }
    }

    private sealed class InlineTranscriptionProvider : ITranscriptionProvider
    {
        public string Provider => "inline-transcription";
        public string Model => "inline-v1";
        public Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionResult(Provider, Model, "inline-request", "synthetic yue transcript", DateTimeOffset.UtcNow));
    }

    private sealed class FailingTranscriptionProvider : ITranscriptionProvider
    {
        public string Provider => "failing-transcription";
        public string Model => "failing-transcribe-v1";
        public Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default)
            => throw new ProviderRequestException("simulated network failure", 503);
    }

    private sealed class RecordingSpeechPlayback : ISpeechOutputPlayback
    {
        public DerivedSpeechAsset? Asset { get; private set; }
        public Task PlayAsync(DerivedSpeechAsset asset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Asset = asset;
            return Task.CompletedTask;
        }
    }

    private sealed class ConversationFixture : IDisposable
    {
        public ConversationFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-conversation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "data", "memory.db");
            AudioPath = Path.Combine(DirectoryPath, "audio.wav");
            MissingPath = Path.Combine(DirectoryPath, "missing.wav");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string AudioPath { get; }
        public string MissingPath { get; }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
