using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>
/// Builds a small, session-scoped context window for a response request. The
/// complete archive is never sent by default; each item remains a transcript
/// interpretation and is labelled as such by the provider adapter.
/// </summary>
public sealed class ConversationContextBuilder
{
    private readonly ArchiveRepository _repository;

    public ConversationContextBuilder(ArchiveRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public string? Build(string sessionId, string? currentSourceId = null, int maxItems = 4)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (maxItems <= 0) throw new ArgumentOutOfRangeException(nameof(maxItems));

        using var connection = _repository.Archive.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.text
            FROM transcript_revisions r
            JOIN sources s ON s.source_id = r.source_id
            WHERE s.session_id = $session
              AND ($current IS NULL OR s.source_id <> $current)
              AND s.recovery_status IN ('finalized', 'recovered')
              AND NOT EXISTS (
                  SELECT 1
                  FROM transcript_revisions newer
                  WHERE newer.source_id = r.source_id
                    AND newer.revision_number > r.revision_number)
            ORDER BY r.created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$current", (object?)currentSourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", maxItems);

        var items = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.GetString(0).Trim();
            if (text.Length > 0) items.Add(text);
        }

        return items.Count == 0 ? null : string.Join("\n", items.AsEnumerable().Reverse());
    }
}
