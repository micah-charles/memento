using Microsoft.Data.Sqlite;
using Memento.Core.Domain;

namespace Memento.Core.Storage;

public sealed class ArchiveRepository(SqliteArchive archive)
{
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

    public Turn AddTurn(string sessionId, int sequenceNumber, string speakerType, DateTimeOffset startedAt, DateTimeOffset? endedAt = null, string? turnId = null)
    {
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

    public SourceMetadata AddSource(SourceMetadata source)
    {
        using var connection = archive.OpenConnection();
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

    public TranscriptRevision AddTranscriptRevision(TranscriptRevision revision)
    {
        using var connection = archive.OpenConnection();
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
        return revision;
    }

    public ClarificationEvent AddClarificationEvent(ClarificationEvent clarification)
    {
        using var connection = archive.OpenConnection();
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

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Format(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
