using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public sealed class ConversationSessionWriter
{
    private readonly ArchiveRepository _repository;

    public ConversationSessionWriter(ArchiveRepository repository) => _repository = repository;

    public Session Start(PrivacyMode privacyMode, DateTimeOffset? startedAt = null)
        => _repository.AddSession(startedAt ?? DateTimeOffset.UtcNow, privacyMode);

    public Turn AddTurn(Session session, int sequenceNumber, string speakerType, DateTimeOffset? startedAt = null, DateTimeOffset? endedAt = null)
        => _repository.AddTurn(session.SessionId, sequenceNumber, speakerType, startedAt ?? DateTimeOffset.UtcNow, endedAt);

    public ConversationJob QueueTranscription(Session session, Turn? turn, SourceMetadata source, DateTimeOffset? now = null)
    {
        return QueueTranscriptionIfNeeded(session, turn, source, now)
            ?? throw new InvalidOperationException("An active transcription job already exists for this Source.");
    }

    public ConversationJob? QueueTranscriptionIfNeeded(Session session, Turn? turn, SourceMetadata source, DateTimeOffset? now = null)
    {
        if (source.SessionId != session.SessionId) throw new InvalidOperationException("The audio Source belongs to another session.");
        if (turn is not null && turn.SessionId != session.SessionId) throw new InvalidOperationException("The turn belongs to another session.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) || string.Equals(_repository.GetSource(source.SourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var job = new ConversationJob(Guid.NewGuid().ToString("N"), session.SessionId, turn?.TurnId, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, timestamp, null, timestamp, timestamp);
        return _repository.TryAddConversationJobIfMissing(job) ? job : null;
    }

    public ConversationJob? QueueExtractionIfNeeded(string sessionId, string? turnId, string sourceId, string transcriptRevisionId, DateTimeOffset? now = null)
    {
        var session = _repository.GetSession(sessionId) ?? throw new InvalidDataException("The extraction session was not found.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode)) return null;
        if (string.Equals(_repository.GetSource(sourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) return null;
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var job = new ConversationJob(Guid.NewGuid().ToString("N"), sessionId, turnId, sourceId, "durable_extraction", ConversationJobStatus.Pending, 0, timestamp, null, timestamp, timestamp, transcriptRevisionId);
        return _repository.TryAddConversationJobIfMissing(job) ? job : null;
    }

    public ConversationJob BeginAttempt(ConversationJob job, DateTimeOffset? now = null)
    {
        return TryBeginAttempt(job, now) ?? throw new InvalidOperationException("Conversation job was already claimed or changed.");
    }

    public ConversationJob? TryBeginAttempt(ConversationJob job, DateTimeOffset? now = null)
    {
        var updated = job with { Status = ConversationJobStatus.Processing, AttemptCount = job.AttemptCount + 1, UpdatedAt = now ?? DateTimeOffset.UtcNow };
        return _repository.TryUpdateConversationJob(job, updated) ? updated : null;
    }

    public ConversationJob MarkSucceeded(ConversationJob job, DateTimeOffset? now = null)
    {
        var updated = job with { Status = ConversationJobStatus.Succeeded, NextAttemptAt = null, LastError = null, UpdatedAt = now ?? DateTimeOffset.UtcNow };
        _repository.UpdateConversationJob(updated);
        return updated;
    }

    public ConversationJob MarkFailed(ConversationJob job, string error, DateTimeOffset? retryAt, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("An error description is required.", nameof(error));
        var updated = job with { Status = ConversationJobStatus.Failed, LastError = error, NextAttemptAt = retryAt, UpdatedAt = now ?? DateTimeOffset.UtcNow };
        _repository.UpdateConversationJob(updated);
        return updated;
    }
}
