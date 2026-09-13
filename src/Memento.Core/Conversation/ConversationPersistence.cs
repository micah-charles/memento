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
        if (source.SessionId != session.SessionId) throw new InvalidOperationException("The audio Source belongs to another session.");
        if (turn is not null && turn.SessionId != session.SessionId) throw new InvalidOperationException("The turn belongs to another session.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase) || string.Equals(_repository.GetSource(source.SourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException();
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return _repository.AddConversationJob(new ConversationJob(Guid.NewGuid().ToString("N"), session.SessionId, turn?.TurnId, source.SourceId, "durable_transcription", ConversationJobStatus.Pending, 0, timestamp, null, timestamp, timestamp));
    }

    public ConversationJob? QueueExtractionIfNeeded(string sessionId, string? turnId, string sourceId, string transcriptRevisionId, DateTimeOffset? now = null)
    {
        if (string.Equals(_repository.GetSource(sourceId)?.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) return null;
        if (_repository.HasActiveConversationJob(sessionId, sourceId, "durable_extraction", transcriptRevisionId)) return null;
        var timestamp = now ?? DateTimeOffset.UtcNow;
        return _repository.AddConversationJob(new ConversationJob(Guid.NewGuid().ToString("N"), sessionId, turnId, sourceId, "durable_extraction", ConversationJobStatus.Pending, 0, timestamp, null, timestamp, timestamp, transcriptRevisionId));
    }

    public ConversationJob BeginAttempt(ConversationJob job, DateTimeOffset? now = null)
    {
        var updated = job with { Status = ConversationJobStatus.Processing, AttemptCount = job.AttemptCount + 1, UpdatedAt = now ?? DateTimeOffset.UtcNow };
        _repository.UpdateConversationJob(updated);
        return updated;
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
