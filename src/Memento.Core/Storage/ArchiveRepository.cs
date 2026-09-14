using Microsoft.Data.Sqlite;
using Memento.Core.Domain;

namespace Memento.Core.Storage;

public sealed class ArchiveRepository(SqliteArchive archive)
{
    public SqliteArchive Archive => archive;

    public Session AddSession(DateTimeOffset startedAt, PrivacyMode privacyMode, string? sessionId = null)
    {
        var session = new Session(sessionId ?? NewId(), startedAt, null, privacyMode, DateTimeOffset.UtcNow);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(session_id, started_at, ended_at, privacy_mode, created_at) VALUES ($id, $started, NULL, $mode, $created)";
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$started", Format(session.StartedAt));
        command.Parameters.AddWithValue("$mode", session.PrivacyMode.ToString());
        command.Parameters.AddWithValue("$created", Format(session.CreatedAt));
        command.ExecuteNonQuery();
        return session;
    }

    public Session EndSession(Session session, DateTimeOffset? endedAt = null)
    {
        var ended = endedAt ?? DateTimeOffset.UtcNow;
        if (ended < session.StartedAt) throw new ArgumentOutOfRangeException(nameof(endedAt), "Session end cannot precede its start.");
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET ended_at = $ended WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$ended", Format(ended));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Session was not found.");
        return session with { EndedAt = ended };
    }

    public Session? GetSession(string sessionId)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id, started_at, ended_at, privacy_mode, created_at FROM sessions WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new Session(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), Enum.Parse<PrivacyMode>(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public Turn AddTurn(string sessionId, int sequenceNumber, string speakerType, DateTimeOffset startedAt, DateTimeOffset? endedAt = null, string? turnId = null)
    {
        if (string.IsNullOrWhiteSpace(speakerType))
            throw new ArgumentException("A speaker type is required.", nameof(speakerType));
        if (sequenceNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "A turn sequence cannot be negative.");
        if (endedAt is not null && endedAt < startedAt)
            throw new ArgumentOutOfRangeException(nameof(endedAt), "Turn end cannot precede its start.");
        var turn = new Turn(turnId ?? NewId(), sessionId, sequenceNumber, speakerType, startedAt, endedAt, DateTimeOffset.UtcNow);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO turns(turn_id, session_id, sequence_number, speaker_type, started_at, ended_at, created_at) VALUES ($id, $session, $sequence, $speaker, $started, $ended, $created)";
        command.Parameters.AddWithValue("$id", turn.TurnId);
        command.Parameters.AddWithValue("$session", turn.SessionId);
        command.Parameters.AddWithValue("$sequence", turn.SequenceNumber);
        command.Parameters.AddWithValue("$speaker", turn.SpeakerType);
        command.Parameters.AddWithValue("$started", Format(turn.StartedAt));
        command.Parameters.AddWithValue("$ended", turn.EndedAt is null ? DBNull.Value : Format(turn.EndedAt.Value));
        command.Parameters.AddWithValue("$created", Format(turn.CreatedAt));
        command.ExecuteNonQuery();
        return turn;
    }

    public Turn EndTurn(Turn turn, DateTimeOffset? endedAt = null)
    {
        var ended = endedAt ?? DateTimeOffset.UtcNow;
        if (ended < turn.StartedAt)
            throw new ArgumentOutOfRangeException(nameof(endedAt), "Turn end cannot precede its start.");
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE turns SET ended_at = $ended WHERE turn_id = $id AND session_id = $session";
        command.Parameters.AddWithValue("$id", turn.TurnId);
        command.Parameters.AddWithValue("$session", turn.SessionId);
        command.Parameters.AddWithValue("$ended", Format(ended));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Turn was not found.");
        return turn with { EndedAt = ended };
    }

    public int GetNextTurnSequence(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence_number) + 1, 0) FROM turns WHERE session_id = $session";
        command.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public ConsentEvent AddConsent(string sessionId, ConsentScope scope, PrivacyMode privacyMode, bool granted, string noticeVersion, string? personId = null, DateTimeOffset? occurredAt = null, string? consentEventId = null)
    {
        var consent = new ConsentEvent(consentEventId ?? NewId(), sessionId, personId, scope, privacyMode, granted, noticeVersion, occurredAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO consent_events(consent_event_id, session_id, person_id, scope, privacy_mode, granted, notice_version, occurred_at, created_at) VALUES ($id, $session, $person, $scope, $mode, $granted, $notice, $occurred, $created)";
        command.Parameters.AddWithValue("$id", consent.ConsentEventId);
        command.Parameters.AddWithValue("$session", consent.SessionId);
        command.Parameters.AddWithValue("$person", (object?)consent.PersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$scope", consent.Scope.ToString());
        command.Parameters.AddWithValue("$mode", consent.PrivacyMode.ToString());
        command.Parameters.AddWithValue("$granted", consent.Granted ? 1 : 0);
        command.Parameters.AddWithValue("$notice", consent.NoticeVersion);
        command.Parameters.AddWithValue("$occurred", Format(consent.OccurredAt));
        command.Parameters.AddWithValue("$created", Format(consent.CreatedAt));
        command.ExecuteNonQuery();
        return consent;
    }

    public bool HasGrantedConsent(string sessionId, ConsentScope scope)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT granted FROM consent_events WHERE session_id = $session AND scope = $scope ORDER BY occurred_at DESC, created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$scope", scope.ToString());
        var value = command.ExecuteScalar();
        return value is not null && Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public SourceMetadata AddSource(SourceMetadata source)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, source.SessionId, source.TurnId, "Source");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sources(source_id, source_type, session_id, turn_id, file_path, format, sample_rate, channels, bit_depth, byte_length, duration_ms, sha256, started_at, finalized_at, recovery_status, created_at)
            VALUES ($id, $type, $session, $turn, $path, $format, $sampleRate, $channels, $bitDepth, $byteLength, $duration, $sha256, $started, $finalized, $recovery, $created)
            """;
        command.Parameters.AddWithValue("$id", source.SourceId);
        command.Parameters.AddWithValue("$type", source.SourceType);
        command.Parameters.AddWithValue("$session", (object?)source.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turn", (object?)source.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)source.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$format", (object?)source.Format ?? DBNull.Value);
        command.Parameters.AddWithValue("$sampleRate", (object?)source.SampleRate ?? DBNull.Value);
        command.Parameters.AddWithValue("$channels", (object?)source.Channels ?? DBNull.Value);
        command.Parameters.AddWithValue("$bitDepth", (object?)source.BitDepth ?? DBNull.Value);
        command.Parameters.AddWithValue("$byteLength", (object?)source.ByteLength ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)source.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha256", (object?)source.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", source.StartedAt is null ? DBNull.Value : Format(source.StartedAt.Value));
        command.Parameters.AddWithValue("$finalized", source.FinalizedAt is null ? DBNull.Value : Format(source.FinalizedAt.Value));
        command.Parameters.AddWithValue("$recovery", source.RecoveryStatus);
        command.Parameters.AddWithValue("$created", Format(source.CreatedAt));
        command.ExecuteNonQuery();
        return source;
    }

    public ProviderInteraction AddProviderInteraction(ProviderInteraction interaction)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, interaction.SessionId, interaction.TurnId, "Provider interaction");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_interactions(provider_interaction_id, session_id, turn_id, provider, capability, model, model_snapshot, request_id, started_at, completed_at, input_audio_ms, output_audio_ms, succeeded, error_code, error_message, created_at)
            VALUES ($id, $session, $turn, $provider, $capability, $model, $snapshot, $request, $started, $completed, $inputAudio, $outputAudio, $succeeded, $errorCode, $errorMessage, $created)
            """;
        command.Parameters.AddWithValue("$id", interaction.ProviderInteractionId);
        command.Parameters.AddWithValue("$session", interaction.SessionId);
        command.Parameters.AddWithValue("$turn", (object?)interaction.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", interaction.Provider);
        command.Parameters.AddWithValue("$capability", interaction.Capability);
        command.Parameters.AddWithValue("$model", interaction.Model);
        command.Parameters.AddWithValue("$snapshot", (object?)interaction.ModelSnapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$request", (object?)interaction.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", Format(interaction.StartedAt));
        command.Parameters.AddWithValue("$completed", interaction.CompletedAt is null ? DBNull.Value : Format(interaction.CompletedAt.Value));
        command.Parameters.AddWithValue("$inputAudio", (object?)interaction.InputAudioMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputAudio", (object?)interaction.OutputAudioMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$succeeded", interaction.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$errorCode", (object?)interaction.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)interaction.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(interaction.CreatedAt));
        command.ExecuteNonQuery();
        return interaction;
    }

    public DerivedSpeechAsset AddDerivedSpeechAsset(DerivedSpeechAsset asset)
    {
        if (asset.ByteLength <= 0) throw new ArgumentOutOfRangeException(nameof(asset), "Derived speech output must contain bytes.");
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, asset.SessionId, asset.TurnId, "Derived speech asset");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO derived_speech_assets(derived_speech_asset_id, session_id, turn_id, file_path, format, byte_length, sha256, provider, model, voice, request_id, created_at)
            VALUES ($id, $session, $turn, $path, $format, $length, $sha256, $provider, $model, $voice, $request, $created)
            """;
        command.Parameters.AddWithValue("$id", asset.DerivedSpeechAssetId);
        command.Parameters.AddWithValue("$session", asset.SessionId);
        command.Parameters.AddWithValue("$turn", (object?)asset.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", asset.FilePath);
        command.Parameters.AddWithValue("$format", asset.Format);
        command.Parameters.AddWithValue("$length", asset.ByteLength);
        command.Parameters.AddWithValue("$sha256", asset.Sha256);
        command.Parameters.AddWithValue("$provider", asset.Provider);
        command.Parameters.AddWithValue("$model", asset.Model);
        command.Parameters.AddWithValue("$voice", asset.Voice);
        command.Parameters.AddWithValue("$request", (object?)asset.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(asset.CreatedAt));
        command.ExecuteNonQuery();
        return asset;
    }

    public IReadOnlyList<DerivedSpeechAsset> ListDerivedSpeechAssets(string sessionId)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT derived_speech_asset_id, session_id, turn_id, file_path, format, byte_length, sha256, provider, model, voice, request_id, created_at FROM derived_speech_assets WHERE session_id = $session ORDER BY created_at";
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        var assets = new List<DerivedSpeechAsset>();
        while (reader.Read())
        {
            assets.Add(new DerivedSpeechAsset(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return assets;
    }

    public DerivedSpeechAsset? GetLatestDerivedSpeechAsset()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT derived_speech_asset_id, session_id, turn_id, file_path, format, byte_length, sha256, provider, model, voice, request_id, created_at FROM derived_speech_assets ORDER BY created_at DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new DerivedSpeechAsset(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public TranscriptRevision AddTranscriptRevision(TranscriptRevision revision)
    {
        using var connection = archive.OpenConnection();
        EnsureSourceContext(connection, revision.SourceId, null, revision.TurnId, "Transcript revision");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transcript_revisions(transcript_revision_id, source_id, turn_id, revision_number, revision_kind, text, confidence, parent_revision_id, created_at)
            VALUES ($id, $source, $turn, $number, $kind, $text, $confidence, $parent, $created)
            """;
        command.Parameters.AddWithValue("$id", revision.TranscriptRevisionId);
        command.Parameters.AddWithValue("$source", revision.SourceId);
        command.Parameters.AddWithValue("$turn", (object?)revision.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$number", revision.RevisionNumber);
        command.Parameters.AddWithValue("$kind", revision.RevisionKind);
        command.Parameters.AddWithValue("$text", revision.Text);
        command.Parameters.AddWithValue("$confidence", (object?)revision.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$parent", (object?)revision.ParentRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(revision.CreatedAt));
        command.ExecuteNonQuery();
        ArchiveSearchIndex.Upsert(connection, null, "transcript_revision", revision.TranscriptRevisionId, revision.Text, revision.SourceId, null);
        return revision;
    }

    public IReadOnlyList<TranscriptRevision> ListTranscriptRevisions(string sourceId)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT transcript_revision_id, source_id, turn_id, revision_number, revision_kind, text, confidence, parent_revision_id, created_at FROM transcript_revisions WHERE source_id = $source ORDER BY revision_number";
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var revisions = new List<TranscriptRevision>();
        while (reader.Read()) revisions.Add(new TranscriptRevision(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return revisions;
    }

    public string? GetSourceFilePath(string sourceId)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM sources WHERE source_id = $source";
        command.Parameters.AddWithValue("$source", sourceId);
        return command.ExecuteScalar()?.ToString();
    }

    public SourceMetadata? GetSource(string sourceId)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, source_type, session_id, turn_id, file_path, format, sample_rate, channels, bit_depth, byte_length, duration_ms, sha256, started_at, finalized_at, recovery_status, created_at FROM sources WHERE source_id = $source";
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceMetadata(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetString(14), DateTimeOffset.Parse(reader.GetString(15), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public SourceMetadata? GetLatestFinalizedSource()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, source_type, session_id, turn_id, file_path, format, sample_rate, channels, bit_depth, byte_length, duration_ms, sha256, started_at, finalized_at, recovery_status, created_at FROM sources WHERE recovery_status IN ('finalized', 'recovered') ORDER BY finalized_at DESC, created_at DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceMetadata(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetString(14), DateTimeOffset.Parse(reader.GetString(15), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public ClarificationEvent AddClarificationEvent(ClarificationEvent clarification)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, clarification.SessionId, clarification.TurnId, "Clarification event");
        EnsureSourceContext(connection, clarification.SourceId, clarification.SessionId, clarification.TurnId, "Clarification event");
        EnsureRevisionContext(connection, clarification.SourceId, clarification.TurnId, clarification.InitialRevisionId, "Clarification event initial revision");
        if (clarification.CorrectedRevisionId is not null)
            EnsureRevisionContext(connection, clarification.SourceId, clarification.TurnId, clarification.CorrectedRevisionId, "Clarification event corrected revision");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clarification_events(clarification_event_id, session_id, turn_id, source_id, trigger_kind, question_text, initial_revision_id, participant_response_text, corrected_revision_id, outcome, occurred_at, created_at)
            VALUES ($id, $session, $turn, $source, $trigger, $question, $initial, $response, $corrected, $outcome, $occurred, $created)
            """;
        command.Parameters.AddWithValue("$id", clarification.ClarificationEventId);
        command.Parameters.AddWithValue("$session", clarification.SessionId);
        command.Parameters.AddWithValue("$turn", (object?)clarification.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", clarification.SourceId);
        command.Parameters.AddWithValue("$trigger", clarification.TriggerKind);
        command.Parameters.AddWithValue("$question", clarification.QuestionText);
        command.Parameters.AddWithValue("$initial", clarification.InitialRevisionId);
        command.Parameters.AddWithValue("$response", (object?)clarification.ParticipantResponseText ?? DBNull.Value);
        command.Parameters.AddWithValue("$corrected", (object?)clarification.CorrectedRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", clarification.Outcome.ToString());
        command.Parameters.AddWithValue("$occurred", Format(clarification.OccurredAt));
        command.Parameters.AddWithValue("$created", Format(clarification.CreatedAt));
        command.ExecuteNonQuery();
        return clarification;
    }

    public VocabularyEntry AddVocabularyEntry(VocabularyEntry entry)
    {
        using var connection = archive.OpenConnection();
        if (entry.SpeakerConfirmed)
            EnsureSpeakerConfirmationEvent(connection, entry.SourceClarificationEventId, "Speaker-confirmed vocabulary");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vocabulary_entries(vocabulary_entry_id, canonical_text, previous_recognition, context, speaker_confirmed, source_clarification_event_id, created_at)
            VALUES ($id, $canonical, $previous, $context, $confirmed, $event, $created)
            """;
        command.Parameters.AddWithValue("$id", entry.VocabularyEntryId);
        command.Parameters.AddWithValue("$canonical", entry.CanonicalText);
        command.Parameters.AddWithValue("$previous", entry.PreviousRecognition);
        command.Parameters.AddWithValue("$context", (object?)entry.Context ?? DBNull.Value);
        command.Parameters.AddWithValue("$confirmed", entry.SpeakerConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$event", entry.SourceClarificationEventId);
        command.Parameters.AddWithValue("$created", Format(entry.CreatedAt));
        command.ExecuteNonQuery();
        return entry;
    }

    /// <summary>
    /// Persists a clarification event together with its optional corrected
    /// revision and vocabulary entry. The correction chain is one unit: a
    /// failure after the revision insert cannot leave an orphaned correction.
    /// </summary>
    public void AddClarificationChain(TranscriptRevision? corrected, ClarificationEvent clarification, VocabularyEntry? vocabulary)
    {
        if (corrected is null && clarification.CorrectedRevisionId is not null)
            throw new InvalidOperationException("The clarification event references a missing corrected revision.");
        if (corrected is not null && !string.Equals(corrected.TranscriptRevisionId, clarification.CorrectedRevisionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The clarification corrected revision does not match its event.");
        if (vocabulary is not null && !string.Equals(vocabulary.SourceClarificationEventId, clarification.ClarificationEventId, StringComparison.Ordinal))
            throw new InvalidOperationException("The vocabulary entry does not reference its clarification event.");

        using var connection = archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureTurnBelongsToSession(connection, clarification.SessionId, clarification.TurnId, "Clarification event", transaction);
        EnsureSourceContext(connection, clarification.SourceId, clarification.SessionId, clarification.TurnId, "Clarification event", transaction);
        EnsureRevisionContext(connection, clarification.SourceId, clarification.TurnId, clarification.InitialRevisionId, "Clarification event initial revision", transaction);
        if (corrected is not null)
        {
            if (corrected.ParentRevisionId is null || !string.Equals(corrected.ParentRevisionId, clarification.InitialRevisionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The corrected revision must point to the clarification initial revision.");
            EnsureSourceContext(connection, corrected.SourceId, clarification.SessionId, corrected.TurnId, "Corrected transcript revision", transaction);
            EnsureRevisionContext(connection, corrected.SourceId, corrected.TurnId, corrected.ParentRevisionId, "Corrected transcript parent revision", transaction);
            using var revision = connection.CreateCommand();
            revision.Transaction = transaction;
            revision.CommandText = "INSERT INTO transcript_revisions(transcript_revision_id, source_id, turn_id, revision_number, revision_kind, text, confidence, parent_revision_id, created_at) VALUES ($id, $source, $turn, $number, $kind, $text, $confidence, $parent, $created)";
            revision.Parameters.AddWithValue("$id", corrected.TranscriptRevisionId);
            revision.Parameters.AddWithValue("$source", corrected.SourceId);
            revision.Parameters.AddWithValue("$turn", (object?)corrected.TurnId ?? DBNull.Value);
            revision.Parameters.AddWithValue("$number", corrected.RevisionNumber);
            revision.Parameters.AddWithValue("$kind", corrected.RevisionKind);
            revision.Parameters.AddWithValue("$text", corrected.Text);
            revision.Parameters.AddWithValue("$confidence", (object?)corrected.Confidence ?? DBNull.Value);
            revision.Parameters.AddWithValue("$parent", corrected.ParentRevisionId);
            revision.Parameters.AddWithValue("$created", Format(corrected.CreatedAt));
            revision.ExecuteNonQuery();
            ArchiveSearchIndex.Upsert(connection, transaction, "transcript_revision", corrected.TranscriptRevisionId, corrected.Text, corrected.SourceId, null);
        }

        if (clarification.CorrectedRevisionId is not null)
            EnsureRevisionContext(connection, clarification.SourceId, clarification.TurnId, clarification.CorrectedRevisionId, "Clarification event corrected revision", transaction);
        using (var eventCommand = connection.CreateCommand())
        {
            eventCommand.Transaction = transaction;
            eventCommand.CommandText = "INSERT INTO clarification_events(clarification_event_id, session_id, turn_id, source_id, trigger_kind, question_text, initial_revision_id, participant_response_text, corrected_revision_id, outcome, occurred_at, created_at) VALUES ($id, $session, $turn, $source, $trigger, $question, $initial, $response, $corrected, $outcome, $occurred, $created)";
            eventCommand.Parameters.AddWithValue("$id", clarification.ClarificationEventId);
            eventCommand.Parameters.AddWithValue("$session", clarification.SessionId);
            eventCommand.Parameters.AddWithValue("$turn", (object?)clarification.TurnId ?? DBNull.Value);
            eventCommand.Parameters.AddWithValue("$source", clarification.SourceId);
            eventCommand.Parameters.AddWithValue("$trigger", clarification.TriggerKind);
            eventCommand.Parameters.AddWithValue("$question", clarification.QuestionText);
            eventCommand.Parameters.AddWithValue("$initial", clarification.InitialRevisionId);
            eventCommand.Parameters.AddWithValue("$response", (object?)clarification.ParticipantResponseText ?? DBNull.Value);
            eventCommand.Parameters.AddWithValue("$corrected", (object?)clarification.CorrectedRevisionId ?? DBNull.Value);
            eventCommand.Parameters.AddWithValue("$outcome", clarification.Outcome.ToString());
            eventCommand.Parameters.AddWithValue("$occurred", Format(clarification.OccurredAt));
            eventCommand.Parameters.AddWithValue("$created", Format(clarification.CreatedAt));
            eventCommand.ExecuteNonQuery();
        }

        if (vocabulary is not null)
        {
            if (!vocabulary.SpeakerConfirmed)
                throw new InvalidOperationException("Clarification vocabulary entries must be speaker-confirmed.");
            EnsureSpeakerConfirmationEvent(connection, vocabulary.SourceClarificationEventId, "Speaker-confirmed vocabulary", transaction);
            using var vocabularyCommand = connection.CreateCommand();
            vocabularyCommand.Transaction = transaction;
            vocabularyCommand.CommandText = "INSERT INTO vocabulary_entries(vocabulary_entry_id, canonical_text, previous_recognition, context, speaker_confirmed, source_clarification_event_id, created_at) VALUES ($id, $canonical, $previous, $context, $confirmed, $event, $created)";
            vocabularyCommand.Parameters.AddWithValue("$id", vocabulary.VocabularyEntryId);
            vocabularyCommand.Parameters.AddWithValue("$canonical", vocabulary.CanonicalText);
            vocabularyCommand.Parameters.AddWithValue("$previous", vocabulary.PreviousRecognition);
            vocabularyCommand.Parameters.AddWithValue("$context", (object?)vocabulary.Context ?? DBNull.Value);
            vocabularyCommand.Parameters.AddWithValue("$confirmed", 1);
            vocabularyCommand.Parameters.AddWithValue("$event", vocabulary.SourceClarificationEventId);
            vocabularyCommand.Parameters.AddWithValue("$created", Format(vocabulary.CreatedAt));
            vocabularyCommand.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ConversationJob AddConversationJob(ConversationJob job)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, job.SessionId, job.TurnId, "Conversation job");
        EnsureSourceContext(connection, job.SourceId, job.SessionId, job.TurnId, "Conversation job");
        if (job.TranscriptRevisionId is not null)
            EnsureRevisionContext(connection, job.SourceId, job.TurnId, job.TranscriptRevisionId, "Conversation job transcript revision");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_jobs(conversation_job_id, session_id, turn_id, source_id, job_type, status, attempt_count, next_attempt_at, last_error, created_at, updated_at, transcript_revision_id)
            VALUES ($id, $session, $turn, $source, $type, $status, $attempt, $next, $error, $created, $updated, $revision)
            """;
        command.Parameters.AddWithValue("$id", job.ConversationJobId);
        command.Parameters.AddWithValue("$session", job.SessionId);
        command.Parameters.AddWithValue("$turn", (object?)job.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", job.SourceId);
        command.Parameters.AddWithValue("$type", job.JobType);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$attempt", job.AttemptCount);
        command.Parameters.AddWithValue("$next", job.NextAttemptAt is null ? DBNull.Value : Format(job.NextAttemptAt.Value));
        command.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(job.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(job.UpdatedAt));
        command.Parameters.AddWithValue("$revision", (object?)job.TranscriptRevisionId ?? DBNull.Value);
        command.ExecuteNonQuery();
        return job;
    }

    /// <summary>Atomically inserts a conversation job when no equivalent active job exists.</summary>
    public bool TryAddConversationJobIfMissing(ConversationJob job)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, job.SessionId, job.TurnId, "Conversation job");
        EnsureSourceContext(connection, job.SourceId, job.SessionId, job.TurnId, "Conversation job");
        if (job.TranscriptRevisionId is not null)
            EnsureRevisionContext(connection, job.SourceId, job.TurnId, job.TranscriptRevisionId, "Conversation job transcript revision");
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_jobs(conversation_job_id, session_id, turn_id, source_id, job_type, status, attempt_count, next_attempt_at, last_error, created_at, updated_at, transcript_revision_id)
            SELECT $id, $session, $turn, $source, $type, $status, $attempt, $next, $error, $created, $updated, $revision
            WHERE NOT EXISTS (
                SELECT 1 FROM conversation_jobs
                WHERE session_id = $session AND source_id = $source AND job_type = $type
                  AND (($revision IS NULL AND transcript_revision_id IS NULL) OR ($revision IS NOT NULL AND transcript_revision_id = $revision))
                  AND (status IN ('Pending', 'Processing', 'Succeeded') OR (status = 'Failed' AND next_attempt_at IS NOT NULL))
            )
            """;
        command.Parameters.AddWithValue("$id", job.ConversationJobId);
        command.Parameters.AddWithValue("$session", job.SessionId);
        command.Parameters.AddWithValue("$turn", (object?)job.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", job.SourceId);
        command.Parameters.AddWithValue("$type", job.JobType);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$attempt", job.AttemptCount);
        command.Parameters.AddWithValue("$next", job.NextAttemptAt is null ? DBNull.Value : Format(job.NextAttemptAt.Value));
        command.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(job.CreatedAt));
        command.Parameters.AddWithValue("$updated", Format(job.UpdatedAt));
        command.Parameters.AddWithValue("$revision", (object?)job.TranscriptRevisionId ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    public void UpdateConversationJob(ConversationJob job)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation_jobs SET status = $status, attempt_count = $attempt, next_attempt_at = $next, last_error = $error, updated_at = $updated, transcript_revision_id = $revision WHERE conversation_job_id = $id";
        command.Parameters.AddWithValue("$id", job.ConversationJobId);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$attempt", job.AttemptCount);
        command.Parameters.AddWithValue("$next", job.NextAttemptAt is null ? DBNull.Value : Format(job.NextAttemptAt.Value));
        command.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", Format(job.UpdatedAt));
        command.Parameters.AddWithValue("$revision", (object?)job.TranscriptRevisionId ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Conversation job was not found.");
    }

    public bool TryUpdateConversationJob(ConversationJob expected, ConversationJob updated)
    {
        if (!string.Equals(expected.ConversationJobId, updated.ConversationJobId, StringComparison.Ordinal))
            throw new ArgumentException("The expected and updated jobs must have the same ID.", nameof(updated));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation_jobs SET status = $status, attempt_count = $attempt, next_attempt_at = $next, last_error = $error, updated_at = $updated, transcript_revision_id = $revision WHERE conversation_job_id = $id AND status = $expectedStatus AND updated_at = $expectedUpdated";
        command.Parameters.AddWithValue("$id", updated.ConversationJobId);
        command.Parameters.AddWithValue("$status", updated.Status.ToString());
        command.Parameters.AddWithValue("$attempt", updated.AttemptCount);
        command.Parameters.AddWithValue("$next", updated.NextAttemptAt is null ? DBNull.Value : Format(updated.NextAttemptAt.Value));
        command.Parameters.AddWithValue("$error", (object?)updated.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", Format(updated.UpdatedAt));
        command.Parameters.AddWithValue("$revision", (object?)updated.TranscriptRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$expectedStatus", expected.Status.ToString());
        command.Parameters.AddWithValue("$expectedUpdated", Format(expected.UpdatedAt));
        return command.ExecuteNonQuery() == 1;
    }

    public bool HasActiveConversationJob(string sessionId, string sourceId, string jobType, string? transcriptRevisionId = null)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversation_jobs WHERE session_id = $session AND source_id = $source AND job_type = $type AND (($revision IS NULL AND transcript_revision_id IS NULL) OR ($revision IS NOT NULL AND transcript_revision_id = $revision)) AND (status IN ('Pending', 'Processing', 'Succeeded') OR (status = 'Failed' AND next_attempt_at IS NOT NULL))";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$type", jobType);
        command.Parameters.AddWithValue("$revision", (object?)transcriptRevisionId ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public IReadOnlyList<ConversationJob> ListRetryableConversationJobs(DateTimeOffset now, TimeSpan? processingLease = null)
    {
        var lease = processingLease ?? TimeSpan.FromMinutes(5);
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processingLease), "Processing lease must be positive.");
        var staleBefore = now - lease;
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_job_id, session_id, turn_id, source_id, job_type, status, attempt_count, next_attempt_at, last_error, created_at, updated_at, transcript_revision_id FROM conversation_jobs WHERE (status = 'Pending' AND (next_attempt_at IS NULL OR next_attempt_at <= $now)) OR (status = 'Failed' AND next_attempt_at IS NOT NULL AND next_attempt_at <= $now) OR (status = 'Processing' AND updated_at <= $staleBefore) ORDER BY created_at";
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$staleBefore", Format(staleBefore));
        using var reader = command.ExecuteReader();
        var jobs = new List<ConversationJob>();
        while (reader.Read()) jobs.Add(new ConversationJob(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<ConversationJobStatus>(reader.GetString(5)), reader.GetInt32(6), reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(8) ? null : reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(11) ? null : reader.GetString(11)));
        return jobs;
    }

    public EvidenceRecord AddEvidence(EvidenceRecord evidence)
    {
        using var connection = archive.OpenConnection();
        EnsureTurnBelongsToSession(connection, evidence.SessionId, evidence.TurnId, "Evidence");
        EnsureSourceContext(connection, evidence.SourceId, evidence.SessionId, evidence.TurnId, "Evidence");
        if (evidence.TranscriptRevisionId is not null)
            EnsureRevisionContext(connection, evidence.SourceId, evidence.TurnId, evidence.TranscriptRevisionId, "Evidence transcript revision");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO evidence_records(evidence_id, kind, source_id, session_id, turn_id, transcript_revision_id, statement, original_expression, participant_certainty, speaker_confirmed, created_at, audio_start_ms, audio_end_ms, extraction_provider, extraction_model) VALUES ($id, $kind, $source, $session, $turn, $revision, $statement, $original, $certainty, $confirmed, $created, $audioStart, $audioEnd, $provider, $model)";
        command.Parameters.AddWithValue("$id", evidence.EvidenceId);
        command.Parameters.AddWithValue("$kind", evidence.Kind.ToString());
        command.Parameters.AddWithValue("$source", evidence.SourceId);
        command.Parameters.AddWithValue("$session", (object?)evidence.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turn", (object?)evidence.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)evidence.TranscriptRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$statement", evidence.Statement);
        command.Parameters.AddWithValue("$original", evidence.OriginalExpression);
        command.Parameters.AddWithValue("$certainty", evidence.ParticipantCertainty.ToString());
        command.Parameters.AddWithValue("$confirmed", evidence.SpeakerConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$created", Format(evidence.CreatedAt));
        command.Parameters.AddWithValue("$audioStart", (object?)evidence.AudioStartMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$audioEnd", (object?)evidence.AudioEndMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", (object?)evidence.ExtractionProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)evidence.ExtractionModel ?? DBNull.Value);
        command.ExecuteNonQuery();
        ArchiveSearchIndex.Upsert(connection, null, "evidence", evidence.EvidenceId, evidence.Statement + " " + evidence.OriginalExpression, evidence.SourceId, evidence.SessionId);
        return evidence;
    }

    public MemoryClaim AddMemoryClaim(MemoryClaim claim)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO memory_claims(memory_claim_id, statement, subject_person_id, predicate, object, status, created_at) VALUES ($id, $statement, $subject, $predicate, $object, $status, $created)";
        command.Parameters.AddWithValue("$id", claim.MemoryClaimId);
        command.Parameters.AddWithValue("$statement", claim.Statement);
        command.Parameters.AddWithValue("$subject", (object?)claim.SubjectPersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$predicate", claim.Predicate);
        command.Parameters.AddWithValue("$object", claim.Object);
        command.Parameters.AddWithValue("$status", claim.Status.ToString());
        command.Parameters.AddWithValue("$created", Format(claim.CreatedAt));
        command.ExecuteNonQuery();
        ArchiveSearchIndex.Upsert(connection, null, "memory_claim", claim.MemoryClaimId, claim.Statement + " " + claim.Predicate + " " + claim.Object, null, null);
        return claim;
    }

    public EvidenceClaimLink AddEvidenceClaimLink(EvidenceClaimLink link)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO evidence_claim_links(evidence_id, memory_claim_id, relationship, created_at) VALUES ($evidence, $claim, $relationship, $created)";
        command.Parameters.AddWithValue("$evidence", link.EvidenceId);
        command.Parameters.AddWithValue("$claim", link.MemoryClaimId);
        command.Parameters.AddWithValue("$relationship", link.Relationship);
        command.Parameters.AddWithValue("$created", Format(link.CreatedAt));
        command.ExecuteNonQuery();
        return link;
    }

    /// <summary>
    /// Persists one extraction result as an all-or-nothing batch. Evidence,
    /// candidate claims, links, and their search rows must not be left half
    /// written if a later row fails (for example after a disk or constraint
    /// error).
    /// </summary>
    public void AddMemoryExtractionBatch(IReadOnlyList<(EvidenceRecord Evidence, MemoryClaim Claim, EvidenceClaimLink Link)> entries)
    {
        if (entries.Count == 0) return;
        using var connection = archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            EnsureTurnBelongsToSession(connection, entry.Evidence.SessionId, entry.Evidence.TurnId, "Evidence", transaction);
            EnsureSourceContext(connection, entry.Evidence.SourceId, entry.Evidence.SessionId, entry.Evidence.TurnId, "Evidence", transaction);
            if (entry.Evidence.TranscriptRevisionId is not null)
                EnsureRevisionContext(connection, entry.Evidence.SourceId, entry.Evidence.TurnId, entry.Evidence.TranscriptRevisionId, "Evidence transcript revision", transaction);

            using (var evidence = connection.CreateCommand())
            {
                evidence.Transaction = transaction;
                evidence.CommandText = "INSERT INTO evidence_records(evidence_id, kind, source_id, session_id, turn_id, transcript_revision_id, statement, original_expression, participant_certainty, speaker_confirmed, created_at, audio_start_ms, audio_end_ms, extraction_provider, extraction_model) VALUES ($id, $kind, $source, $session, $turn, $revision, $statement, $original, $certainty, $confirmed, $created, $audioStart, $audioEnd, $provider, $model)";
                evidence.Parameters.AddWithValue("$id", entry.Evidence.EvidenceId);
                evidence.Parameters.AddWithValue("$kind", entry.Evidence.Kind.ToString());
                evidence.Parameters.AddWithValue("$source", entry.Evidence.SourceId);
                evidence.Parameters.AddWithValue("$session", (object?)entry.Evidence.SessionId ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$turn", (object?)entry.Evidence.TurnId ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$revision", (object?)entry.Evidence.TranscriptRevisionId ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$statement", entry.Evidence.Statement);
                evidence.Parameters.AddWithValue("$original", entry.Evidence.OriginalExpression);
                evidence.Parameters.AddWithValue("$certainty", entry.Evidence.ParticipantCertainty.ToString());
                evidence.Parameters.AddWithValue("$confirmed", entry.Evidence.SpeakerConfirmed ? 1 : 0);
                evidence.Parameters.AddWithValue("$created", Format(entry.Evidence.CreatedAt));
                evidence.Parameters.AddWithValue("$audioStart", (object?)entry.Evidence.AudioStartMs ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$audioEnd", (object?)entry.Evidence.AudioEndMs ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$provider", (object?)entry.Evidence.ExtractionProvider ?? DBNull.Value);
                evidence.Parameters.AddWithValue("$model", (object?)entry.Evidence.ExtractionModel ?? DBNull.Value);
                evidence.ExecuteNonQuery();
            }
            ArchiveSearchIndex.Upsert(connection, transaction, "evidence", entry.Evidence.EvidenceId, entry.Evidence.Statement + " " + entry.Evidence.OriginalExpression, entry.Evidence.SourceId, entry.Evidence.SessionId);

            using (var claim = connection.CreateCommand())
            {
                claim.Transaction = transaction;
                claim.CommandText = "INSERT INTO memory_claims(memory_claim_id, statement, subject_person_id, predicate, object, status, created_at) VALUES ($id, $statement, $subject, $predicate, $object, $status, $created)";
                claim.Parameters.AddWithValue("$id", entry.Claim.MemoryClaimId);
                claim.Parameters.AddWithValue("$statement", entry.Claim.Statement);
                claim.Parameters.AddWithValue("$subject", (object?)entry.Claim.SubjectPersonId ?? DBNull.Value);
                claim.Parameters.AddWithValue("$predicate", entry.Claim.Predicate);
                claim.Parameters.AddWithValue("$object", entry.Claim.Object);
                claim.Parameters.AddWithValue("$status", entry.Claim.Status.ToString());
                claim.Parameters.AddWithValue("$created", Format(entry.Claim.CreatedAt));
                claim.ExecuteNonQuery();
            }
            ArchiveSearchIndex.Upsert(connection, transaction, "memory_claim", entry.Claim.MemoryClaimId, entry.Claim.Statement + " " + entry.Claim.Predicate + " " + entry.Claim.Object, null, null);

            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT INTO evidence_claim_links(evidence_id, memory_claim_id, relationship, created_at) VALUES ($evidence, $claim, $relationship, $created)";
            link.Parameters.AddWithValue("$evidence", entry.Link.EvidenceId);
            link.Parameters.AddWithValue("$claim", entry.Link.MemoryClaimId);
            link.Parameters.AddWithValue("$relationship", entry.Link.Relationship);
            link.Parameters.AddWithValue("$created", Format(entry.Link.CreatedAt));
            link.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public PersonEntity AddPersonEntity(PersonEntity entity)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO person_entities(person_entity_id, display_name, relationship, created_at) VALUES ($id, $name, $relationship, $created)";
        command.Parameters.AddWithValue("$id", entity.PersonEntityId);
        command.Parameters.AddWithValue("$name", entity.DisplayName);
        command.Parameters.AddWithValue("$relationship", (object?)entity.Relationship ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(entity.CreatedAt));
        command.ExecuteNonQuery();
        return entity;
    }

    public IReadOnlyList<PersonEntity> ListPersonEntities()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT person_entity_id, display_name, relationship, created_at FROM person_entities ORDER BY created_at, person_entity_id";
        using var reader = command.ExecuteReader();
        var people = new List<PersonEntity>();
        while (reader.Read())
        {
            people.Add(new PersonEntity(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return people;
    }

    public bool PersonEntityExists(string personEntityId)
    {
        if (string.IsNullOrWhiteSpace(personEntityId)) return false;
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM person_entities WHERE person_entity_id = $id)";
        command.Parameters.AddWithValue("$id", personEntityId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public EntityAlias AddEntityAlias(EntityAlias alias)
    {
        using var connection = archive.OpenConnection();
        if (alias.SpeakerConfirmed)
            EnsureSpeakerConfirmationEvent(connection, alias.SourceClarificationEventId, "Speaker-confirmed entity alias");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO entity_aliases(entity_alias_id, person_entity_id, alias, speaker_confirmed, source_clarification_event_id, created_at) VALUES ($id, $entity, $alias, $confirmed, $event, $created)";
        command.Parameters.AddWithValue("$id", alias.EntityAliasId);
        command.Parameters.AddWithValue("$entity", alias.PersonEntityId);
        command.Parameters.AddWithValue("$alias", alias.Alias);
        command.Parameters.AddWithValue("$confirmed", alias.SpeakerConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$event", (object?)alias.SourceClarificationEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(alias.CreatedAt));
        command.ExecuteNonQuery();
        return alias;
    }

    public IReadOnlyList<EntityAlias> ListEntityAliases()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_alias_id, person_entity_id, alias, speaker_confirmed, source_clarification_event_id, created_at FROM entity_aliases ORDER BY created_at, entity_alias_id";
        using var reader = command.ExecuteReader();
        var aliases = new List<EntityAlias>();
        while (reader.Read())
        {
            aliases.Add(new EntityAlias(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return aliases;
    }

    public EvidenceEntityLink AddEvidenceEntityLink(EvidenceEntityLink link)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO evidence_entity_links(evidence_id, person_entity_id, role, created_at) VALUES ($evidence, $person, $role, $created)";
        command.Parameters.AddWithValue("$evidence", link.EvidenceId);
        command.Parameters.AddWithValue("$person", link.PersonEntityId);
        command.Parameters.AddWithValue("$role", link.Role);
        command.Parameters.AddWithValue("$created", Format(link.CreatedAt));
        command.ExecuteNonQuery();
        return link;
    }

    public IReadOnlyList<MemoryClaim> ListCandidateClaims()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.memory_claim_id, c.statement, c.subject_person_id, c.predicate, c.object, c.status, c.created_at
            FROM memory_claims c
            WHERE c.status = 'Candidate'
              AND NOT EXISTS (
                  SELECT 1
                  FROM evidence_claim_links l
                  JOIN evidence_records e ON e.evidence_id = l.evidence_id
                  JOIN sources s ON s.source_id = e.source_id AND s.recovery_status = 'withdrawn'
                  WHERE l.memory_claim_id = c.memory_claim_id
              )
            ORDER BY c.created_at
            """;
        using var reader = command.ExecuteReader();
        var claims = new List<MemoryClaim>();
        while (reader.Read()) claims.Add(new MemoryClaim(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<ClaimStatus>(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return claims;
    }

    public IReadOnlyList<MemoryClaim> ListReviewedClaims()
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.memory_claim_id, c.statement, c.subject_person_id, c.predicate, c.object, c.status, c.created_at
            FROM memory_claims c
            WHERE c.status = 'Reviewed'
              AND NOT EXISTS (
                  SELECT 1
                  FROM evidence_claim_links l
                  JOIN evidence_records e ON e.evidence_id = l.evidence_id
                  JOIN sources s ON s.source_id = e.source_id AND s.recovery_status = 'withdrawn'
                  WHERE l.memory_claim_id = c.memory_claim_id
              )
            ORDER BY c.created_at, c.memory_claim_id
            """;
        using var reader = command.ExecuteReader();
        var claims = new List<MemoryClaim>();
        while (reader.Read()) claims.Add(new MemoryClaim(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<ClaimStatus>(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return claims;
    }

    public IReadOnlyList<ClaimEvidence> ListEvidenceForClaim(string claimId)
    {
        if (string.IsNullOrWhiteSpace(claimId)) throw new ArgumentException("A claim ID is required.", nameof(claimId));
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.evidence_id, e.kind, e.source_id, e.session_id, e.turn_id, e.transcript_revision_id,
                   e.statement, e.original_expression, e.participant_certainty, e.speaker_confirmed,
                   e.created_at, e.audio_start_ms, e.audio_end_ms, e.extraction_provider, e.extraction_model,
                   l.relationship
            FROM evidence_records e
            JOIN evidence_claim_links l ON l.evidence_id = e.evidence_id
            WHERE l.memory_claim_id = $claim
            ORDER BY e.created_at, e.evidence_id
            """;
        command.Parameters.AddWithValue("$claim", claimId);
        using var reader = command.ExecuteReader();
        var evidence = new List<ClaimEvidence>();
        while (reader.Read())
        {
            var record = new EvidenceRecord(
                reader.GetString(0),
                Enum.Parse<EvidenceKind>(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                Enum.Parse<ParticipantCertainty>(reader.GetString(8)),
                reader.GetInt64(9) == 1,
                DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(11) ? null : reader.GetInt64(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14));
            evidence.Add(new ClaimEvidence(record, reader.GetString(15)));
        }

        return evidence;
    }

    public void UpdateMemoryClaimStatus(string claimId, ClaimStatus status)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE memory_claims SET status = $status WHERE memory_claim_id = $id";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$id", claimId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Memory claim was not found.");
    }

    public ReviewAnnotation AddReviewAnnotation(ReviewAnnotation annotation)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO review_annotations(annotation_id, target_type, target_id, actor_id, annotation_type, body, assessment, created_at) VALUES ($id, $type, $target, $actor, $annotation, $body, $assessment, $created)";
        command.Parameters.AddWithValue("$id", annotation.AnnotationId);
        command.Parameters.AddWithValue("$type", annotation.TargetType);
        command.Parameters.AddWithValue("$target", annotation.TargetId);
        command.Parameters.AddWithValue("$actor", annotation.ActorId);
        command.Parameters.AddWithValue("$annotation", annotation.AnnotationType);
        command.Parameters.AddWithValue("$body", annotation.Body);
        command.Parameters.AddWithValue("$assessment", (object?)annotation.Assessment ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Format(annotation.CreatedAt));
        command.ExecuteNonQuery();
        return annotation;
    }

    public ResponseEpisode AddResponseEpisode(ResponseEpisode episode)
    {
        using var connection = archive.OpenConnection();
        EnsureEvidenceBelongsToSession(connection, episode.StimulusEvidenceId, episode.SessionId, "Response episode stimulus");
        EnsureEvidenceBelongsToSession(connection, episode.ResponseEvidenceId, episode.SessionId, "Response episode response");
        if (episode.FollowUpEvidenceId is not null)
            EnsureEvidenceBelongsToSession(connection, episode.FollowUpEvidenceId, episode.SessionId, "Response episode follow-up");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO response_episodes(response_episode_id, session_id, stimulus_evidence_id, response_evidence_id, follow_up_evidence_id, observed_details, observation_basis, created_at) VALUES ($id, $session, $stimulus, $response, $followUp, $details, $basis, $created)";
        command.Parameters.AddWithValue("$id", episode.ResponseEpisodeId);
        command.Parameters.AddWithValue("$session", episode.SessionId);
        command.Parameters.AddWithValue("$stimulus", episode.StimulusEvidenceId);
        command.Parameters.AddWithValue("$response", episode.ResponseEvidenceId);
        command.Parameters.AddWithValue("$followUp", (object?)episode.FollowUpEvidenceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", episode.ObservedDetails);
        command.Parameters.AddWithValue("$basis", episode.ObservationBasis);
        command.Parameters.AddWithValue("$created", Format(episode.CreatedAt));
        command.ExecuteNonQuery();
        return episode;
    }

    public AppSetting SetSetting(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A setting key is required.", nameof(key));
        var setting = new AppSetting(key, value ?? string.Empty, DateTimeOffset.UtcNow);
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_settings(setting_key, setting_value, updated_at) VALUES ($key, $value, $updated) ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value, updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("$key", setting.Key);
        command.Parameters.AddWithValue("$value", setting.Value);
        command.Parameters.AddWithValue("$updated", Format(setting.UpdatedAt));
        command.ExecuteNonQuery();
        return setting;
    }

    public string? GetSetting(string key)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM app_settings WHERE setting_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Format(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static void EnsureTurnBelongsToSession(SqliteConnection connection, string? sessionId, string? turnId, string context, SqliteTransaction? transaction = null)
    {
        if (sessionId is null || turnId is null) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM turns WHERE turn_id = $turn";
        command.Parameters.AddWithValue("$turn", turnId);
        var turnSessionId = command.ExecuteScalar()?.ToString();
        if (turnSessionId is not null && !string.Equals(turnSessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} turn does not belong to the supplied session.");
    }

    private static void EnsureSourceContext(SqliteConnection connection, string sourceId, string? sessionId, string? turnId, string context, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id, turn_id FROM sources WHERE source_id = $source";
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;

        var sourceSessionId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var sourceTurnId = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (sessionId is not null && !string.Equals(sourceSessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} does not belong to the supplied session.");
        if (!string.Equals(sourceTurnId, turnId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} does not belong to the supplied turn.");
    }

    private static void EnsureRevisionContext(SqliteConnection connection, string sourceId, string? turnId, string revisionId, string context, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source_id, turn_id FROM transcript_revisions WHERE transcript_revision_id = $revision";
        command.Parameters.AddWithValue("$revision", revisionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;

        if (!string.Equals(reader.GetString(0), sourceId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} does not belong to the supplied source.");
        var revisionTurnId = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (turnId is not null && !string.Equals(revisionTurnId, turnId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} does not belong to the supplied turn.");
    }

    private static void EnsureEvidenceBelongsToSession(SqliteConnection connection, string evidenceId, string sessionId, string context, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT session_id FROM evidence_records WHERE evidence_id = $evidence";
        command.Parameters.AddWithValue("$evidence", evidenceId);
        var evidenceSessionId = command.ExecuteScalar()?.ToString();
        if (evidenceSessionId is not null && !string.Equals(evidenceSessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} does not belong to the supplied session.");
    }

    private static void EnsureSpeakerConfirmationEvent(SqliteConnection connection, string? clarificationEventId, string context, SqliteTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(clarificationEventId))
            throw new InvalidOperationException($"{context} requires a speaker-confirmed clarification event.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT outcome FROM clarification_events WHERE clarification_event_id = $event";
        command.Parameters.AddWithValue("$event", clarificationEventId);
        var outcome = command.ExecuteScalar()?.ToString();
        if (outcome is not null
            && !string.Equals(outcome, nameof(ClarificationOutcome.SpeakerConfirmed), StringComparison.Ordinal)
            && !string.Equals(outcome, nameof(ClarificationOutcome.CorrectedPreviousCorrection), StringComparison.Ordinal))
            throw new InvalidOperationException($"{context} requires a speaker-confirmed clarification outcome.");
    }
}
