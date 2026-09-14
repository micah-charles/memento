using System.Text.Json;
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
        var orchestrator = new RealtimeConversationOrchestrator(repository, provider);

        var result = await orchestrator.ExecuteAsync(new RealtimeConversationRequest(
            session.SessionId, null, fixture.AudioPath, PrivacyMode.Normal, true,
            DateTimeOffset.UtcNow, source.SourceId));

        Assert.Equal("stub response", result.Response.Text);
        Assert.Equal(1, provider.Calls);
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
