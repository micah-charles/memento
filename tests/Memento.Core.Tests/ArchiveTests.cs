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
        Assert.Equal(5, archive.CurrentSchemaVersion);
        Assert.True(archive.IsIntegrityCheckClean());
        using var connection = archive.OpenConnection();
        Assert.Equal("1", Scalar(connection, "PRAGMA foreign_keys"));
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

        Assert.Equal(5, reopened.CurrentSchemaVersion);
        using var connection = reopened.OpenConnection();
        Assert.Equal(5L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations")));
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
            repository.AddSource(new SourceMetadata("source-fixed", "placeholder", session.SessionId, turn.TurnId, "raw/audio/placeholder.wav", "PCM WAV", 48000, 1, 24, 0, 0, null, null, null, "not_applicable", DateTimeOffset.UtcNow));
        }

        using var reopened = new SqliteArchive(fixture.DatabasePath);
        reopened.Initialize();
        using var connection = reopened.OpenConnection();
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sessions WHERE session_id = 'session-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM turns WHERE turn_id = 'turn-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM consent_events WHERE consent_event_id = 'consent-fixed'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM sources WHERE source_id = 'source-fixed'")));
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
