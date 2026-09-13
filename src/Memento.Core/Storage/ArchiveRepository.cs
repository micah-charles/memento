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

    public ConversationJob AddConversationJob(ConversationJob job)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_jobs(conversation_job_id, session_id, turn_id, source_id, job_type, status, attempt_count, next_attempt_at, last_error, created_at, updated_at)
            VALUES ($id, $session, $turn, $source, $type, $status, $attempt, $next, $error, $created, $updated)
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
        command.ExecuteNonQuery();
        return job;
    }

    public void UpdateConversationJob(ConversationJob job)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversation_jobs SET status = $status, attempt_count = $attempt, next_attempt_at = $next, last_error = $error, updated_at = $updated WHERE conversation_job_id = $id";
        command.Parameters.AddWithValue("$id", job.ConversationJobId);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$attempt", job.AttemptCount);
        command.Parameters.AddWithValue("$next", job.NextAttemptAt is null ? DBNull.Value : Format(job.NextAttemptAt.Value));
        command.Parameters.AddWithValue("$error", (object?)job.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", Format(job.UpdatedAt));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Conversation job was not found.");
    }

    public IReadOnlyList<ConversationJob> ListRetryableConversationJobs(DateTimeOffset now)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_job_id, session_id, turn_id, source_id, job_type, status, attempt_count, next_attempt_at, last_error, created_at, updated_at FROM conversation_jobs WHERE status IN ('Pending', 'Failed') AND (next_attempt_at IS NULL OR next_attempt_at <= $now) ORDER BY created_at";
        command.Parameters.AddWithValue("$now", Format(now));
        using var reader = command.ExecuteReader();
        var jobs = new List<ConversationJob>();
        while (reader.Read()) jobs.Add(new ConversationJob(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<ConversationJobStatus>(reader.GetString(5)), reader.GetInt32(6), reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(8) ? null : reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind), DateTimeOffset.Parse(reader.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return jobs;
    }

    public EvidenceRecord AddEvidence(EvidenceRecord evidence)
    {
        using var connection = archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO evidence_records(evidence_id, kind, source_id, session_id, turn_id, transcript_revision_id, statement, original_expression, participant_certainty, speaker_confirmed, created_at) VALUES ($id, $kind, $source, $session, $turn, $revision, $statement, $original, $certainty, $confirmed, $created)";
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
        command.ExecuteNonQuery();
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

    public EntityAlias AddEntityAlias(EntityAlias alias)
    {
        using var connection = archive.OpenConnection();
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
        command.CommandText = "SELECT memory_claim_id, statement, subject_person_id, predicate, object, status, created_at FROM memory_claims WHERE status = 'Candidate' ORDER BY created_at";
        using var reader = command.ExecuteReader();
        var claims = new List<MemoryClaim>();
        while (reader.Read()) claims.Add(new MemoryClaim(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), Enum.Parse<ClaimStatus>(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return claims;
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

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Format(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
