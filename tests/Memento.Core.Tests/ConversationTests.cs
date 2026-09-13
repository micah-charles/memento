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

    private sealed class ProviderUnavailableException() : InvalidOperationException;

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
