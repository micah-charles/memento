using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed class SqliteArchive : IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteArchive(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        DatabasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public int CurrentSchemaVersion
    {
        get
        {
            using var connection = OpenConnection();
            return GetCurrentSchemaVersion(connection);
        }
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");

        var applied = GetAppliedVersions(connection, transaction);
        foreach (var migration in Migrations.All)
        {
            if (applied.Contains(migration.Version))
                continue;

            migration.Apply(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at) VALUES ($version, $appliedAt)";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$appliedAt", Timestamp.UtcNow());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public SqliteConnection OpenConnection()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON";
            foreignKeys.ExecuteNonQuery();
        }

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL";
            journal.ExecuteScalar();
        }

        return connection;
    }

    public bool IsIntegrityCheckClean()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<int> GetAppliedVersions(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_migrations";
        using var reader = command.ExecuteReader();
        var versions = new HashSet<int>();
        while (reader.Read())
            versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static int GetCurrentSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
        try
        {
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    internal static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteArchive));
    }

    public void Dispose() => _disposed = true;
}

internal static class Migrations
{
    internal static readonly Migration[] All =
    [
        new(1, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE sessions (
                session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                privacy_mode TEXT NOT NULL CHECK (privacy_mode IN ('Normal', 'PrivateConversation', 'LocalCaptureOnly')),
                created_at TEXT NOT NULL
            );
            CREATE TABLE turns (
                turn_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                sequence_number INTEGER NOT NULL CHECK (sequence_number >= 0),
                speaker_type TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(session_id, sequence_number)
            );
            CREATE TABLE consent_events (
                consent_event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                person_id TEXT NULL,
                scope TEXT NOT NULL CHECK (scope IN ('LocalCapture', 'CloudTranscription', 'LiveCloudConversation', 'FamilyAdminSharing')),
                privacy_mode TEXT NOT NULL CHECK (privacy_mode IN ('Normal', 'PrivateConversation', 'LocalCaptureOnly')),
                granted INTEGER NOT NULL CHECK (granted IN (0, 1)),
                notice_version TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE sources (
                source_id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                session_id TEXT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                file_path TEXT NULL,
                format TEXT NULL,
                sample_rate INTEGER NULL CHECK (sample_rate IS NULL OR sample_rate > 0),
                channels INTEGER NULL CHECK (channels IS NULL OR channels > 0),
                bit_depth INTEGER NULL CHECK (bit_depth IS NULL OR bit_depth > 0),
                byte_length INTEGER NULL CHECK (byte_length IS NULL OR byte_length >= 0),
                duration_ms INTEGER NULL CHECK (duration_ms IS NULL OR duration_ms >= 0),
                sha256 TEXT NULL,
                started_at TEXT NULL,
                finalized_at TEXT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX ix_turns_session ON turns(session_id, sequence_number);
            CREATE INDEX ix_sources_session ON sources(session_id);
            """)),
        new(2, (connection, transaction) => SqliteArchive.Execute(connection, transaction, "ALTER TABLE sources ADD COLUMN recovery_status TEXT NOT NULL DEFAULT 'not_applicable'"))
    ];

    internal sealed record Migration(int Version, Action<SqliteConnection, SqliteTransaction> Apply);
}

internal static class Timestamp
{
    internal static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
