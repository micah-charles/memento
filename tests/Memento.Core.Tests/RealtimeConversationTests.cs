using System.Text.Json;
using System.Threading.Channels;
using Memento.Core.Audio;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class RealtimeConversationTests
{
    [Fact]
    public async Task Websocket_provider_streams_local_pcm_and_collects_audio_and_text_events()
    {
        using var fixture = new RealtimeFixture();
        var transport = new FakeRealtimeTransport(
            "{\"type\":\"session.created\",\"event_id\":\"evt_session\"}",
            "{\"type\":\"conversation.item.input_audio_transcription.completed\",\"transcript\":\"你好\"}",
            "{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"你好，\"}",
            "{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"我係 MEMENTO。\"}",
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AQID\"}",
            "{\"type\":\"response.done\",\"event_id\":\"evt_done\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\"}}" );
        var provider = new OpenAiRealtimeWebSocketProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            "gpt-test",
            () => transport,
            new Uri("wss://example.test/v1/realtime?model=gpt-test"));

        var result = await provider.SendAsync(new RealtimeConversationRequest(
            "session", "turn", fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow));

        Assert.Equal("你好，我係 MEMENTO。", result.Response.Text);
        Assert.Equal("resp_1", result.Response.RequestId);
        Assert.Equal("你好", result.InputTranscript);
        Assert.Equal([1, 2, 3], result.OutputAudioPcm);
        Assert.Equal(10L, result.Response.InputAudioMs);
        Assert.Equal(0L, result.Response.OutputAudioMs);
        Assert.True(transport.Messages.Count >= 4);
        Assert.Contains(transport.Messages, message => message.Contains("session.update", StringComparison.Ordinal));
        Assert.Contains(transport.Messages, message => message.Contains("input_audio_buffer.append", StringComparison.Ordinal));
        Assert.Contains(transport.Messages, message => message.Contains("input_audio_buffer.commit", StringComparison.Ordinal));
        Assert.Contains(transport.Messages, message => message.Contains("response.create", StringComparison.Ordinal));
        var sessionUpdate = transport.Messages.Single(message => message.Contains("session.update", StringComparison.Ordinal));
        using var sessionDocument = JsonDocument.Parse(sessionUpdate);
        var session = sessionDocument.RootElement.GetProperty("session");
        Assert.Equal("realtime", session.GetProperty("type").GetString());
        Assert.Equal("audio", session.GetProperty("output_modalities")[0].GetString());
        Assert.Equal("audio/pcm", session.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, session.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32());
        Assert.True(session.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind == JsonValueKind.Null);
        Assert.Equal("gpt-transcribe", session.GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal("{\"type\":\"response.create\"}", transport.Messages.Single(message => message.Contains("response.create", StringComparison.Ordinal)));
        var append = transport.Messages.Single(message => message.Contains("input_audio_buffer.append", StringComparison.Ordinal));
        using var appendDocument = JsonDocument.Parse(append);
        Assert.Equal(Convert.ToBase64String(fixture.PcmBytes), appendDocument.RootElement.GetProperty("audio").GetString());
    }

    [Fact]
    public async Task Realtime_transcription_model_can_be_configured_without_changing_voice_model()
    {
        using var fixture = new RealtimeFixture();
        var transport = new FakeRealtimeTransport(
            "{\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}",
            "{\"type\":\"response.done\",\"response\":{\"id\":\"resp-configured\",\"status\":\"completed\"}}");
        var provider = new OpenAiRealtimeWebSocketProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            model: "gpt-realtime-2.1-mini",
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime"),
            transcriptionModel: "gpt-4o-transcribe");

        await provider.SendAsync(new RealtimeConversationRequest(
            "session-configured", null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow));

        var sessionUpdate = transport.Messages.Single(message => message.Contains("session.update", StringComparison.Ordinal));
        using var sessionDocument = JsonDocument.Parse(sessionUpdate);
        var session = sessionDocument.RootElement.GetProperty("session");
        Assert.Equal("gpt-realtime-2.1-mini", session.GetProperty("model").GetString());
        Assert.Equal("gpt-4o-transcribe", session.GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal("gpt-4o-transcribe", provider.TranscriptionModel);
    }

    [Fact]
    public async Task Websocket_provider_requires_live_consent_before_connecting()
    {
        using var fixture = new RealtimeFixture();
        var transport = new FakeRealtimeTransport();
        var provider = new OpenAiRealtimeWebSocketProvider(
            new DelegateApiCredentialProvider(() => "test-key"), transportFactory: () => transport);

        await Assert.ThrowsAsync<CloudConsentRequiredException>(() => provider.SendAsync(new RealtimeConversationRequest(
            "session", null, fixture.AudioPath, PrivacyMode.Normal, false, DateTimeOffset.UtcNow)));

        Assert.False(transport.Connected);
    }

    [Fact]
    public async Task Websocket_provider_downsamples_memento_48khz_pcm_to_realtime_24khz()
    {
        using var fixture = new RealtimeFixture();
        var sourcePath = Path.Combine(fixture.AudioRoot, "input-48.wav");
        var sourceBytes = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        using (var writer = PcmWaveWriter.Create(fixture.AudioRoot, "session", DateTimeOffset.UtcNow, new PcmWaveFormat(48000, 1, 16), "input-48"))
        {
            writer.Append(sourceBytes);
            var asset = writer.FinalizeAsset();
            File.Move(asset.FilePath, sourcePath);
        }

        var transport = new FakeRealtimeTransport(
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AQ==\"}",
            "{\"type\":\"response.done\",\"response\":{\"status\":\"completed\"}}" );
        var provider = new OpenAiRealtimeWebSocketProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime?model=gpt-realtime-2.1-mini"));

        await provider.SendAsync(new RealtimeConversationRequest(
            "session", null, sourcePath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow));

        var append = transport.Messages.Where(message => message.Contains("input_audio_buffer.append", StringComparison.Ordinal)).ToArray();
        Assert.Single(append);
        using var document = JsonDocument.Parse(append[0]);
        Assert.Equal(Convert.ToBase64String([1, 0, 3, 0]), document.RootElement.GetProperty("audio").GetString());
    }

    [Fact]
    public async Task Streaming_provider_sends_live_pcm_chunks_and_completes_a_response()
    {
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(48000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            "gpt-test",
            () => transport,
            new Uri("wss://example.test/v1/realtime?model=gpt-test"),
            transcriptionModel: "gpt-4o-transcribe");

        await using var session = await provider.StartAsync(new RealtimeStreamingRequest(
            "session", "turn", PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source);
        source.Emit([1, 0, 2, 0, 3, 0, 4, 0]);
        var completion = session.CompleteAsync();
        await transport.WaitForMessageAsync(message => message.Contains("input_audio_buffer.commit", StringComparison.Ordinal));
        await transport.EmitAsync("{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"收到啦\"}");
        await transport.EmitAsync("{\"type\":\"response.output_audio.delta\",\"delta\":\"AQID\"}");
        await transport.EmitAsync("{\"type\":\"response.done\",\"response\":{\"id\":\"stream-response\",\"status\":\"completed\"}}");
        var result = await completion;

        Assert.Equal("收到啦", result.Response.Text);
        Assert.Equal("stream-response", result.Response.RequestId);
        Assert.Equal([1, 2, 3], result.OutputAudioPcm);
        Assert.Contains(transport.Messages, message => message.Contains("session.update", StringComparison.Ordinal));
        Assert.Contains("{\"type\":\"input_audio_buffer.commit\"}", transport.Messages);
        Assert.Contains("{\"type\":\"response.create\"}", transport.Messages);
        var sessionUpdate = transport.Messages.Single(message => message.Contains("session.update", StringComparison.Ordinal));
        using var sessionDocument = JsonDocument.Parse(sessionUpdate);
        Assert.Equal("gpt-4o-transcribe", sessionDocument.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString());
        var append = transport.Messages.Single(message => message.Contains("input_audio_buffer.append", StringComparison.Ordinal));
        using var appendDocument = JsonDocument.Parse(append);
        Assert.Equal(Convert.ToBase64String([1, 0, 3, 0]), appendDocument.RootElement.GetProperty("audio").GetString());
    }

    [Fact]
    public async Task Streaming_session_fails_with_bounded_timeout_when_provider_never_completes()
    {
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime"),
            completionTimeout: TimeSpan.FromMilliseconds(75));

        await using var session = await provider.StartAsync(new RealtimeStreamingRequest(
            "session", null, PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source);
        source.Emit([1, 0, 2, 0]);

        var error = await Assert.ThrowsAsync<ProviderRequestException>(() => session.CompleteAsync());

        Assert.Equal(504, error.StatusCode);
        Assert.Equal("provider request failed (HTTP 504)", ProviderFailureSummary.ForPersistence(error));
    }

    [Fact]
    public async Task Streaming_timeout_persists_content_free_failure_metadata()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime"),
            completionTimeout: TimeSpan.FromMilliseconds(75));
        var live = await new RealtimeStreamingOrchestrator(repository, provider).StartAsync(new RealtimeStreamingRequest(
            session.SessionId, null, PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source);
        await using (live)
        {
            source.Emit([1, 0, 2, 0]);
            await Assert.ThrowsAsync<ProviderRequestException>(() => live.CompleteAsync());
        }

        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT succeeded, error_message FROM provider_interactions WHERE session_id = $session ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", session.SessionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal("provider request failed (HTTP 504)", reader.GetString(1));
    }

    [Fact]
    public async Task Streaming_provider_requires_live_consent_before_connecting()
    {
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime"));

        await Assert.ThrowsAsync<CloudConsentRequiredException>(() => provider.StartAsync(
            new RealtimeStreamingRequest("session", null, PrivacyMode.Normal, false, DateTimeOffset.UtcNow), source));
        Assert.False(transport.Connected);
    }

    [Fact]
    public async Task Streaming_orchestrator_enforces_archive_consent_and_persists_derived_output()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            "gpt-test",
            () => transport,
            new Uri("wss://example.test/v1/realtime?model=gpt-test"));
        var speechStore = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));
        var orchestrator = new RealtimeStreamingOrchestrator(repository, provider, speechStore);

        await using var live = await orchestrator.StartAsync(new RealtimeStreamingRequest(
            session.SessionId, null, PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source);
        source.Emit([1, 0, 2, 0]);
        var completion = live.CompleteAsync();
        await transport.WaitForMessageAsync(message => message.Contains("input_audio_buffer.commit", StringComparison.Ordinal));
        await transport.EmitAsync("{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"完成\"}");
        await transport.EmitAsync("{\"type\":\"response.output_audio.delta\",\"delta\":\"AQI=\"}");
        await transport.EmitAsync("{\"type\":\"response.done\",\"response\":{\"id\":\"archive-stream\",\"status\":\"completed\"}}");
        var result = await completion;

        Assert.Equal("完成", result.Response.Text);
        Assert.NotNull(result.OutputSpeechAsset);
        Assert.Equal("wav", result.OutputSpeechAsset!.Format);
        Assert.Single(repository.ListDerivedSpeechAssets(session.SessionId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capability, succeeded FROM provider_interactions WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session.SessionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("realtime_conversation", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task Streaming_orchestrator_drops_response_after_live_consent_is_revoked()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport();
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            "gpt-test",
            () => transport,
            new Uri("wss://example.test/v1/realtime?model=gpt-test"));
        var speechStore = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));
        var orchestrator = new RealtimeStreamingOrchestrator(repository, provider, speechStore);

        await using var live = await orchestrator.StartAsync(new RealtimeStreamingRequest(
            session.SessionId, null, PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, false, "privacy-1");
        source.Emit([1, 0, 2, 0]);
        var completion = live.CompleteAsync();
        await transport.WaitForMessageAsync(message => message.Contains("input_audio_buffer.commit", StringComparison.Ordinal));
        await transport.EmitAsync("{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"唔應該顯示\"}");
        await transport.EmitAsync("{\"type\":\"response.done\",\"response\":{\"id\":\"revoked-stream\",\"status\":\"completed\"}}");

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => completion);
        Assert.Empty(repository.ListDerivedSpeechAssets(session.SessionId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT succeeded FROM provider_interactions WHERE session_id = $session ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Streaming_orchestrator_rechecks_consent_after_transport_handshake()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        using var source = new FakeAudioChunkSource(new PcmWaveFormat(24000, 1, 16));
        await using var transport = new StreamingFakeRealtimeTransport(() =>
            repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, false, "privacy-1"));
        var provider = new OpenAiRealtimeStreamingProvider(
            new DelegateApiCredentialProvider(() => "test-key"),
            transportFactory: () => transport,
            endpoint: new Uri("wss://example.test/v1/realtime"));
        var orchestrator = new RealtimeStreamingOrchestrator(repository, provider);

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => orchestrator.StartAsync(
            new RealtimeStreamingRequest(session.SessionId, null, PrivacyMode.Normal, true, DateTimeOffset.UtcNow), source));

        Assert.True(transport.Connected);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT succeeded FROM provider_interactions WHERE session_id = $session ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Realtime_orchestrator_requires_live_scope_and_persists_success_metadata()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(new SourceMetadata(
            "source-realtime", "audio", session.SessionId, null, fixture.AudioPath,
            "PCM WAV", 24000, 1, 16, fixture.PcmBytes.LongLength + 44, 20,
            "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var provider = new StubRealtimeProvider();
        var speechStore = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));
        var orchestrator = new RealtimeConversationOrchestrator(repository, provider, speechStore);

        var result = await orchestrator.ExecuteAsync(new RealtimeConversationRequest(
            session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true,
            DateTimeOffset.UtcNow, source.SourceId));

        Assert.Equal("stub response", result.Response.Text);
        Assert.Equal(1, provider.Calls);
        Assert.NotNull(result.OutputSpeechAsset);
        Assert.Equal("wav", result.OutputSpeechAsset!.Format);
        var persistedAudio = speechStore.ReadVerified(result.OutputSpeechAsset);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(persistedAudio, 0, 4));
        Assert.Equal(46, persistedAudio.Length);
        Assert.Single(repository.ListDerivedSpeechAssets(session.SessionId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capability, succeeded FROM provider_interactions WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session.SessionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("realtime_conversation", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task Realtime_orchestrator_does_not_persist_after_source_is_deleted_during_provider_call()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(new SourceMetadata(
            "source-realtime-deleted", "audio", session.SessionId, null, fixture.AudioPath,
            "PCM WAV", 24000, 1, 16, fixture.PcmBytes.LongLength + 44, 20,
            "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var provider = new SourceDeletingRealtimeProvider(repository, source.SourceId);
        var speechStore = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));
        var orchestrator = new RealtimeConversationOrchestrator(repository, provider, speechStore);

        await Assert.ThrowsAsync<InvalidDataException>(() => orchestrator.ExecuteAsync(new RealtimeConversationRequest(
            session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true,
            DateTimeOffset.UtcNow, source.SourceId)));

        Assert.Null(repository.GetSource(source.SourceId));
        Assert.Empty(repository.ListDerivedSpeechAssets(session.SessionId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_interactions WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session.SessionId);
        Assert.Equal(0L, (long)(command.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void Derived_audio_store_rejects_incomplete_realtime_pcm_frames()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var store = new DerivedAudioStore(repository, Path.Combine(fixture.DirectoryPath, "derived", "audio"));

        Assert.Throws<ArgumentException>(() => store.StorePcm(
            "session",
            null,
            new PcmWaveFormat(24000, 1, 16),
            [1],
            "openai",
            "gpt-realtime-2.1-mini"));
        Assert.Empty(repository.ListDerivedSpeechAssets("session"));
    }

    [Fact]
    public async Task Realtime_orchestrator_persists_content_free_provider_failure_metadata()
    {
        using var fixture = new RealtimeFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LiveCloudConversation, PrivacyMode.Normal, true, "privacy-1");
        var provider = new FailingRealtimeProvider();
        var orchestrator = new RealtimeConversationOrchestrator(repository, provider);

        await Assert.ThrowsAsync<ProviderRequestException>(() => orchestrator.ExecuteAsync(new RealtimeConversationRequest(
            session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true, DateTimeOffset.UtcNow)));

        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT error_message FROM provider_interactions WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", session.SessionId);
        var errorMessage = command.ExecuteScalar()?.ToString();
        Assert.Equal("provider request failed (HTTP 429)", errorMessage);
        Assert.DoesNotContain("private transcript", errorMessage, StringComparison.Ordinal);
    }

    private sealed class FakeRealtimeTransport(params string[] events) : IRealtimeMessageTransport
    {
        private readonly Queue<string> _events = new(events);
        public List<string> Messages { get; } = [];
        public bool Connected { get; private set; }

        public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken = default)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_events.Count == 0 ? null : (string?)_events.Dequeue());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamingFakeRealtimeTransport(Action? onConnect = null) : IRealtimeMessageTransport
    {
        private readonly System.Threading.Channels.Channel<string> _events = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public List<string> Messages { get; } = [];
        public bool Connected { get; private set; }

        public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken = default)
        {
            Connected = true;
            onConnect?.Invoke();
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try { return await _events.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }

        public ValueTask EmitAsync(string message) => _events.Writer.WriteAsync(message);

        public async Task WaitForMessageAsync(Func<string, bool> predicate)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (Messages.Any(predicate)) return;
                await Task.Delay(1);
            }
            throw new TimeoutException("Expected realtime message was not sent.");
        }

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAudioChunkSource(PcmWaveFormat format) : IAudioChunkSource, IDisposable
    {
        public PcmWaveFormat Format { get; } = format;
        public event EventHandler<AudioDataEventArgs>? DataAvailable;

        public void Emit(byte[] bytes)
            => DataAvailable?.Invoke(this, new AudioDataEventArgs(bytes, bytes.Length));

        public void Dispose() { }
    }

    private sealed class StubRealtimeProvider : IRealtimeConversationProvider
    {
        public string Provider => "stub";
        public string Model => "stub-model";
        public int Calls { get; private set; }

        public Task<RealtimeConversationResult> SendAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new RealtimeConversationResult(
                new ConversationResponse(Provider, "realtime_conversation", Model, null, "request", "stub response", 100, 0, DateTimeOffset.UtcNow),
                [1, 2], "stub input"));
        }
    }

    private sealed class FailingRealtimeProvider : IRealtimeConversationProvider
    {
        public string Provider => "stub";
        public string Model => "stub-model";

        public Task<RealtimeConversationResult> SendAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default)
            => throw new ProviderRequestException("private transcript must never be persisted", 429);
    }

    private sealed class SourceDeletingRealtimeProvider(ArchiveRepository repository, string sourceId) : IRealtimeConversationProvider
    {
        public string Provider => "source-deleting-realtime";
        public string Model => "source-deleting-realtime-v1";

        public Task<RealtimeConversationResult> SendAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default)
        {
            new Memento.Core.Admin.ArchiveDeletionService(repository, new Memento.Core.Admin.FixedTestAdminAuthorizer("admin"))
                .DeleteSource("admin", sourceId, "test source deletion during realtime conversation");
            return Task.FromResult(new RealtimeConversationResult(
                new ConversationResponse(Provider, "realtime_conversation", Model, null, "request", "stale response", 100, 0, DateTimeOffset.UtcNow),
                [1, 2], "stale input"));
        }
    }

    private sealed class RealtimeFixture : IDisposable
    {
        public RealtimeFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-realtime-tests", Guid.NewGuid().ToString("N"));
            AudioRoot = Path.Combine(DirectoryPath, "raw", "audio");
            Directory.CreateDirectory(AudioRoot);
            DatabasePath = Path.Combine(DirectoryPath, "data", "memory.db");
            AudioPath = Path.Combine(AudioRoot, "input.wav");
            PcmBytes = new byte[480];
            using var writer = PcmWaveWriter.Create(AudioRoot, "session", DateTimeOffset.UtcNow, new PcmWaveFormat(24000, 1, 16), "input");
            writer.Append(PcmBytes);
            var asset = writer.FinalizeAsset();
            File.Move(asset.FilePath, AudioPath);
        }

        public string DirectoryPath { get; }
        public string AudioRoot { get; }
        public string DatabasePath { get; }
        public string AudioPath { get; }
        public byte[] PcmBytes { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
