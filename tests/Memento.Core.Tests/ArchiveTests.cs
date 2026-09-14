using Microsoft.Data.Sqlite;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ArchiveTests
{
    [Fact]
    public void Clean_database_is_created_and_migrated()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);

        archive.Initialize();

        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.Equal(17, archive.CurrentSchemaVersion);
        Assert.True(archive.IsIntegrityCheckClean());
        using var connection = archive.OpenConnection();
        Assert.Equal("1", Scalar(connection, "PRAGMA foreign_keys"));
        Assert.Equal("5000", Scalar(connection, "PRAGMA busy_timeout"));
        Assert.Equal("2", Scalar(connection, "PRAGMA synchronous"));
        Assert.Contains("recovery_status", Columns(connection, "sources"));
        Assert.Contains("provider", Columns(connection, "provider_interactions"));
    }

    [Fact]
    public void Migration_is_repeatable_across_startups()
    {
        using var fixture = new ArchiveFixture();
        using (var first = new SqliteArchive(fixture.DatabasePath))
            first.Initialize();

        using var reopened = new SqliteArchive(fixture.DatabasePath);
        reopened.Initialize();

        Assert.Equal(17, reopened.CurrentSchemaVersion);
        using var connection = reopened.OpenConnection();
        Assert.Equal(17L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations")));
        Assert.True(reopened.IsIntegrityCheckClean());
    }

    [Fact]
    public void Session_turn_consent_and_source_survive_reopen()
    {
        using var fixture = new ArchiveFixture();
        using (var archive = new SqliteArchive(fixture.DatabasePath))
        {
            archive.Initialize();
            var repository = new ArchiveRepository(archive);
            var session = repository.AddSession(DateTimeOffset.Parse("2026-09-13T09:00:00Z"), PrivacyMode.Normal, "session-fixed");
            var turn = repository.AddTurn(session.SessionId, 0, "participant", DateTimeOffset.Parse("2026-09-13T09:00:01Z"), turnId: "turn-fixed");
            repository.AddConsent(session.SessionId, ConsentScope.LocalCapture, PrivacyMode.Normal, true, "privacy-1", personId: "person-1", consentEventId: "consent-fixed");
            repository.AddSource(new SourceMetadata("source-fixed", "placeholder", session.SessionId, turn.TurnId, "raw/audio/placeholder.wav", "PCM WAV", 48000, 1, 24, 0, 0, null, null, null, "finalized", DateTimeOffset.UtcNow));
        }

        using var reopened = new SqliteArchive(fixture.DatabasePath);
        reopened.Initialize();
        using var connection = reopened.OpenConnection();
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sessions WHERE session_id = 'session-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM turns WHERE turn_id = 'turn-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM consent_events WHERE consent_event_id = 'consent-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sources WHERE source_id = 'source-fixed'")));
        var reopenedRepository = new ArchiveRepository(reopened);
        Assert.Equal("session-fixed", reopenedRepository.GetLatestFinalizedSource()!.SessionId);
        Assert.Equal("session-fixed", reopenedRepository.GetSession("session-fixed")!.SessionId);
    }

    [Fact]
    public void Latest_source_includes_a_recovered_audio_asset()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        var recovered = new SourceMetadata(
            "source-recovered-latest", "audio", session.SessionId, null,
            "raw/recovered.wav", "PCM WAV", 16000, 1, 16, 44, 0, "abc",
            DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1),
            "recovered", DateTimeOffset.UtcNow);

        repository.AddSource(recovered);

        Assert.Equal(recovered.SourceId, repository.GetLatestFinalizedSource()!.SourceId);
    }

    [Fact]
    public void Foreign_key_rejects_invalid_relationship()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);

        var error = Assert.Throws<SqliteException>(() => repository.AddTurn("missing-session", 0, "participant", DateTimeOffset.UtcNow));

        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.OrdinalIgnoreCase);

        var firstSession = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var secondSession = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var firstTurn = repository.AddTurn(firstSession.SessionId, 0, "participant", DateTimeOffset.UtcNow);
        var secondTurn = repository.AddTurn(secondSession.SessionId, 0, "participant", DateTimeOffset.UtcNow);
        var source = repository.AddSource(new SourceMetadata("source-context", "audio", firstSession.SessionId, firstTurn.TurnId, null, null, null, null, null, null, null, null, null, null, "not_applicable", DateTimeOffset.UtcNow));
        var sessionlessSource = repository.AddSource(new SourceMetadata("source-sessionless-context", "audio", null, null, null, null, null, null, null, null, null, null, null, null, "not_applicable", DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(() => repository.AddSource(new SourceMetadata("source-context-mismatch", "audio", secondSession.SessionId, firstTurn.TurnId, null, null, null, null, null, null, null, null, null, null, "not_applicable", DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddTranscriptRevision(new TranscriptRevision("revision-context-mismatch", source.SourceId, secondTurn.TurnId, 1, "initial", "mismatch", null, null, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddTranscriptRevision(new TranscriptRevision("revision-context-missing-turn", source.SourceId, null, 1, "initial", "missing turn", null, null, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddProviderInteraction(new ProviderInteraction("interaction-context-mismatch", secondSession.SessionId, firstTurn.TurnId, "test", "transcription", "test", null, null, DateTimeOffset.UtcNow, null, null, null, false, "test", "mismatch", DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddConversationJob(new ConversationJob("job-context-mismatch", secondSession.SessionId, null, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddConversationJob(new ConversationJob("job-context-missing-turn", firstSession.SessionId, null, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddConversationJob(new ConversationJob("job-sessionless-context", firstSession.SessionId, null, sessionlessSource.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var firstRevision = repository.AddTranscriptRevision(new TranscriptRevision("revision-context", source.SourceId, firstTurn.TurnId, 1, "initial", "valid", null, null, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => repository.AddEvidence(new EvidenceRecord("evidence-context-mismatch", EvidenceKind.DirectStatement, source.SourceId, secondSession.SessionId, firstTurn.TurnId, firstRevision.TranscriptRevisionId, "mismatch", "mismatch", ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => repository.AddClarificationEvent(new ClarificationEvent("clarification-context-mismatch", secondSession.SessionId, firstTurn.TurnId, source.SourceId, "name", "?", firstRevision.TranscriptRevisionId, null, null, ClarificationOutcome.ParticipantRefused, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Duplicate_sequence_in_a_session_is_rejected()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.LocalCaptureOnly);
        repository.AddTurn(session.SessionId, 0, "participant", DateTimeOffset.UtcNow);

        Assert.Throws<SqliteException>(() => repository.AddTurn(session.SessionId, 0, "assistant", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Session_end_is_persisted_and_cannot_precede_start()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var started = DateTimeOffset.Parse("2026-09-13T09:00:00Z");
        var session = repository.AddSession(started, PrivacyMode.LocalCaptureOnly);

        var ended = repository.EndSession(session, started.AddMinutes(1));

        Assert.Equal(started.AddMinutes(1), ended.EndedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => repository.EndSession(session, started.AddSeconds(-1)));
    }

    [Fact]
    public void Turn_lifecycle_and_sequence_are_persisted()
    {
        using var fixture = new ArchiveFixture();
        var started = DateTimeOffset.Parse("2026-09-13T09:00:00Z");
        string sessionId;
        using (var archive = new SqliteArchive(fixture.DatabasePath))
        {
            archive.Initialize();
            var repository = new ArchiveRepository(archive);
            var session = repository.AddSession(started, PrivacyMode.LocalCaptureOnly);
            sessionId = session.SessionId;

            Assert.Equal(0, repository.GetNextTurnSequence(session.SessionId));
            var turn = repository.AddTurn(session.SessionId, repository.GetNextTurnSequence(session.SessionId), "participant", started);
            Assert.Equal(1, repository.GetNextTurnSequence(session.SessionId));

            var ended = repository.EndTurn(turn, started.AddMinutes(1));

            Assert.Equal(started.AddMinutes(1), ended.EndedAt);
            Assert.Equal(1, repository.GetNextTurnSequence(session.SessionId));
            Assert.Throws<ArgumentOutOfRangeException>(() => repository.EndTurn(turn, started.AddSeconds(-1)));
        }

        using var reopened = new SqliteArchive(fixture.DatabasePath);
        reopened.Initialize();
        var reopenedRepository = new ArchiveRepository(reopened);
        Assert.Equal(1, reopenedRepository.GetNextTurnSequence(sessionId));
    }

    [Fact]
    public void Recording_setting_defaults_enabled_and_persists_disable()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);

        Assert.Equal("1", repository.GetSetting("recording_enabled"));
        repository.SetSetting("recording_enabled", "0");

        using var reopened = new SqliteArchive(fixture.DatabasePath);
        reopened.Initialize();
        Assert.Equal("0", new ArchiveRepository(reopened).GetSetting("recording_enabled"));
    }

    [Fact]
    public void Latest_consent_event_wins_after_restart()
    {
        using var fixture = new ArchiveFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);

        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        Assert.True(repository.HasGrantedConsent(session.SessionId, ConsentScope.CloudTranscription));

        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, false, "privacy-1");
        Assert.False(repository.HasGrantedConsent(session.SessionId, ConsentScope.CloudTranscription));

        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        Assert.True(repository.HasGrantedConsent(session.SessionId, ConsentScope.CloudTranscription));
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            result.Add(reader.GetString(1));
        return result;
    }

    private sealed class ArchiveFixture : IDisposable
    {
        public ArchiveFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "data", "memory.db");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
