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
        ,new(3, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE provider_interactions (
                provider_interaction_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                provider TEXT NOT NULL,
                capability TEXT NOT NULL,
                model TEXT NOT NULL,
                model_snapshot TEXT NULL,
                request_id TEXT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                input_audio_ms INTEGER NULL CHECK (input_audio_ms IS NULL OR input_audio_ms >= 0),
                output_audio_ms INTEGER NULL CHECK (output_audio_ms IS NULL OR output_audio_ms >= 0),
                succeeded INTEGER NOT NULL CHECK (succeeded IN (0, 1)),
                error_code TEXT NULL,
                error_message TEXT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX ix_provider_interactions_session ON provider_interactions(session_id, started_at);
            """))
        ,new(4, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE transcript_revisions (
                transcript_revision_id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                revision_number INTEGER NOT NULL CHECK (revision_number > 0),
                revision_kind TEXT NOT NULL CHECK (revision_kind IN ('initial', 'corrected')),
                text TEXT NOT NULL,
                confidence REAL NULL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
                parent_revision_id TEXT NULL REFERENCES transcript_revisions(transcript_revision_id) ON DELETE RESTRICT,
                created_at TEXT NOT NULL,
                UNIQUE(source_id, revision_number)
            );
            CREATE TABLE clarification_events (
                clarification_event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
                trigger_kind TEXT NOT NULL,
                question_text TEXT NOT NULL,
                initial_revision_id TEXT NOT NULL REFERENCES transcript_revisions(transcript_revision_id) ON DELETE RESTRICT,
                participant_response_text TEXT NULL,
                corrected_revision_id TEXT NULL REFERENCES transcript_revisions(transcript_revision_id) ON DELETE RESTRICT,
                outcome TEXT NOT NULL CHECK (outcome IN ('SpeakerConfirmed', 'ParticipantRefused', 'ParticipantDoesNotRemember', 'TwoPossibilities', 'CorrectedPreviousCorrection')),
                occurred_at TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE vocabulary_entries (
                vocabulary_entry_id TEXT PRIMARY KEY,
                canonical_text TEXT NOT NULL,
                previous_recognition TEXT NOT NULL,
                context TEXT NULL,
                speaker_confirmed INTEGER NOT NULL CHECK (speaker_confirmed IN (0, 1)),
                source_clarification_event_id TEXT NOT NULL REFERENCES clarification_events(clarification_event_id) ON DELETE RESTRICT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX ix_transcript_revisions_source ON transcript_revisions(source_id, revision_number);
            CREATE INDEX ix_clarification_events_session ON clarification_events(session_id, occurred_at);
            CREATE INDEX ix_vocabulary_entries_canonical ON vocabulary_entries(canonical_text);
            """))
        ,new(5, (connection, transaction) => SqliteArchive.Execute(connection, transaction, "CREATE INDEX ix_clarification_events_initial ON clarification_events(initial_revision_id)"))
        ,new(6, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE conversation_jobs (
                conversation_job_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
                job_type TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('Pending', 'Processing', 'Succeeded', 'Failed')),
                attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
                next_attempt_at TEXT NULL,
                last_error TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX ix_conversation_jobs_status ON conversation_jobs(status, next_attempt_at);
            """))
        ,new(7, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE evidence_records (
                evidence_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL CHECK (kind IN ('DirectStatement', 'ConfirmedInterpretation', 'AiInference', 'SystemObservation', 'ExternalFact')),
                source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
                session_id TEXT NULL REFERENCES sessions(session_id) ON DELETE RESTRICT,
                turn_id TEXT NULL REFERENCES turns(turn_id) ON DELETE RESTRICT,
                transcript_revision_id TEXT NULL REFERENCES transcript_revisions(transcript_revision_id) ON DELETE RESTRICT,
                statement TEXT NOT NULL,
                original_expression TEXT NOT NULL,
                participant_certainty TEXT NOT NULL CHECK (participant_certainty IN ('Stated', 'Uncertain', 'Unknown', 'NotApplicable')),
                speaker_confirmed INTEGER NOT NULL CHECK (speaker_confirmed IN (0, 1)),
                created_at TEXT NOT NULL
            );
            CREATE TABLE memory_claims (
                memory_claim_id TEXT PRIMARY KEY,
                statement TEXT NOT NULL,
                subject_person_id TEXT NULL,
                predicate TEXT NOT NULL,
                object TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('Candidate', 'Reviewed', 'Rejected')),
                created_at TEXT NOT NULL
            );
            CREATE TABLE evidence_claim_links (
                evidence_id TEXT NOT NULL REFERENCES evidence_records(evidence_id) ON DELETE RESTRICT,
                memory_claim_id TEXT NOT NULL REFERENCES memory_claims(memory_claim_id) ON DELETE RESTRICT,
                relationship TEXT NOT NULL CHECK (relationship IN ('supports', 'weakens', 'contradicts', 'clarifies', 'contextualises')),
                created_at TEXT NOT NULL,
                PRIMARY KEY (evidence_id, memory_claim_id, relationship)
            );
            CREATE INDEX ix_evidence_source ON evidence_records(source_id, created_at);
            CREATE INDEX ix_claim_links_claim ON evidence_claim_links(memory_claim_id);
            """))
        ,new(8, (connection, transaction) => SqliteArchive.Execute(connection, transaction, """
            CREATE TABLE person_entities (
                person_entity_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                relationship TEXT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE entity_aliases (
                entity_alias_id TEXT PRIMARY KEY,
                person_entity_id TEXT NOT NULL REFERENCES person_entities(person_entity_id) ON DELETE RESTRICT,
                alias TEXT NOT NULL,
                speaker_confirmed INTEGER NOT NULL CHECK (speaker_confirmed IN (0, 1)),
                source_clarification_event_id TEXT NULL REFERENCES clarification_events(clarification_event_id) ON DELETE RESTRICT,
                created_at TEXT NOT NULL,
                UNIQUE(person_entity_id, alias)
            );
            CREATE TABLE evidence_entity_links (
                evidence_id TEXT NOT NULL REFERENCES evidence_records(evidence_id) ON DELETE RESTRICT,
                person_entity_id TEXT NOT NULL REFERENCES person_entities(person_entity_id) ON DELETE RESTRICT,
                role TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (evidence_id, person_entity_id, role)
            );
            CREATE INDEX ix_entity_aliases_alias ON entity_aliases(alias);
            """))
    ];

    internal sealed record Migration(int Version, Action<SqliteConnection, SqliteTransaction> Apply);
}

internal static class Timestamp
{
    internal static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
