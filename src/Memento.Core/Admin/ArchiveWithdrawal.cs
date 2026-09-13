using Memento.Core.Storage;

namespace Memento.Core.Admin;

public sealed record ArchiveWithdrawalResult(string SourceId, string ActorId, bool Changed, string AnnotationId, DateTimeOffset OccurredAt);

/// <summary>Quarantines a Source from future AI use while retaining its historical records.</summary>
public sealed class ArchiveWithdrawalService
{
    private readonly ArchiveRepository _repository;
    private readonly IAdminAuthorizer _authorizer;

    public ArchiveWithdrawalService(ArchiveRepository repository, IAdminAuthorizer authorizer)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    public ArchiveWithdrawalResult WithdrawSource(string actorId, string sourceId, string reason, DateTimeOffset? occurredAt = null)
    {
        DemandAuthorization(actorId);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A Source ID is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A withdrawal reason is required.", nameof(reason));

        var annotationId = Guid.NewGuid().ToString("N");
        var timestamp = occurredAt ?? DateTimeOffset.UtcNow;
        var changed = false;
        using var connection = _repository.Archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "SELECT recovery_status FROM sources WHERE source_id = $source";
            source.Parameters.AddWithValue("$source", sourceId);
            var status = source.ExecuteScalar()?.ToString();
            if (status is null) throw new KeyNotFoundException($"Source '{sourceId}' was not found.");
            if (!string.Equals(status, "withdrawn", StringComparison.OrdinalIgnoreCase))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE sources SET recovery_status = 'withdrawn' WHERE source_id = $source";
                update.Parameters.AddWithValue("$source", sourceId);
                changed = update.ExecuteNonQuery() == 1;
            }
        }

        using (var annotation = connection.CreateCommand())
        {
            annotation.Transaction = transaction;
            annotation.CommandText = "INSERT INTO review_annotations(annotation_id, target_type, target_id, actor_id, annotation_type, body, assessment, created_at) VALUES ($id, 'source', $target, $actor, 'withdrawal', $body, NULL, $created)";
            annotation.Parameters.AddWithValue("$id", annotationId);
            annotation.Parameters.AddWithValue("$target", sourceId);
            annotation.Parameters.AddWithValue("$actor", actorId);
            annotation.Parameters.AddWithValue("$body", "Source withdrawn from future AI use. Reason: " + reason.Trim());
            annotation.Parameters.AddWithValue("$created", timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            annotation.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ArchiveWithdrawalResult(sourceId, actorId, changed, annotationId, timestamp);
    }

    private void DemandAuthorization(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !_authorizer.IsAuthorized(actorId))
            throw new UnauthorizedAccessException("Family Admin authorization is required for withdrawal.");
    }
}
