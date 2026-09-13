using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed record ArchiveSearchHit(
    string RecordType,
    string RecordId,
    string Content,
    string? SourceId,
    string? SessionId);

/// <summary>Rebuildable local lexical search over transcript, Evidence, and candidate Claim text.</summary>
public sealed class ArchiveSearchService(SqliteArchive archive)
{
    public int Rebuild()
    {
        using var connection = archive.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM memory_search";
            clear.ExecuteNonQuery();
        }

        using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            INSERT INTO memory_search(content, source_id, record_type, record_id, session_id)
                SELECT text, source_id, 'transcript_revision', transcript_revision_id, NULL FROM transcript_revisions;
            INSERT INTO memory_search(content, source_id, record_type, record_id, session_id)
                SELECT statement || ' ' || original_expression, source_id, 'evidence', evidence_id, session_id FROM evidence_records;
            INSERT INTO memory_search(content, source_id, record_type, record_id, session_id)
                SELECT statement || ' ' || predicate || ' ' || object, NULL, 'memory_claim', memory_claim_id, NULL FROM memory_claims;
            """;
        rebuild.ExecuteNonQuery();
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM memory_search";
        var rows = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        transaction.Commit();
        return rows;
    }

    public IReadOnlyList<ArchiveSearchHit> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be between 1 and 100.");

        using var connection = archive.OpenConnection();
        var results = new List<ArchiveSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ftsQuery = BuildFtsQuery(query);
        if (!string.IsNullOrWhiteSpace(ftsQuery))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.record_type, d.record_id, d.content, d.source_id, d.session_id
                FROM memory_search d
                LEFT JOIN sources s ON s.source_id = d.source_id
                WHERE memory_search MATCH $query
                  AND (d.source_id IS NULL OR s.recovery_status IS NULL OR s.recovery_status <> 'withdrawn')
                  AND (
                    d.record_type <> 'memory_claim'
                    OR NOT EXISTS (
                        SELECT 1
                        FROM evidence_claim_links l
                        JOIN evidence_records e ON e.evidence_id = l.evidence_id
                        JOIN sources ws ON ws.source_id = e.source_id AND ws.recovery_status = 'withdrawn'
                        WHERE l.memory_claim_id = d.record_id
                    )
                  )
                ORDER BY bm25(memory_search)
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read()) AddResult(results, seen, reader);
        }

        if (results.Count < limit)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.record_type, d.record_id, d.content, d.source_id, d.session_id
                FROM memory_search d
                LEFT JOIN sources s ON s.source_id = d.source_id
                WHERE d.content LIKE $pattern ESCAPE '\'
                  AND (d.source_id IS NULL OR s.recovery_status IS NULL OR s.recovery_status <> 'withdrawn')
                  AND (
                    d.record_type <> 'memory_claim'
                    OR NOT EXISTS (
                        SELECT 1
                        FROM evidence_claim_links l
                        JOIN evidence_records e ON e.evidence_id = l.evidence_id
                        JOIN sources ws ON ws.source_id = e.source_id AND ws.recovery_status = 'withdrawn'
                        WHERE l.memory_claim_id = d.record_id
                    )
                  )
                ORDER BY d.record_type, d.record_id
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(query.Trim()) + "%");
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read() && results.Count < limit) AddResult(results, seen, reader);
        }

        return results;
    }

    private static void AddResult(List<ArchiveSearchHit> results, HashSet<string> seen, SqliteDataReader reader)
    {
        var recordType = reader.GetString(0);
        var recordId = reader.GetString(1);
        if (!seen.Add(recordType + ":" + recordId)) return;
        results.Add(new ArchiveSearchHit(recordType, recordId, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = new List<string>();
        var latin = new System.Text.StringBuilder();
        foreach (var character in query.Trim())
        {
            if (IsCjk(character))
            {
                FlushLatin(tokens, latin);
                tokens.Add('"' + character.ToString().Replace("\"", "\"\"") + '"');
            }
            else if (char.IsLetterOrDigit(character) || character == '_')
            {
                latin.Append(char.ToLowerInvariant(character));
            }
            else
            {
                FlushLatin(tokens, latin);
            }
        }

        FlushLatin(tokens, latin);
        return string.Join(" AND ", tokens);
    }

    private static void FlushLatin(List<string> tokens, System.Text.StringBuilder latin)
    {
        if (latin.Length == 0) return;
        tokens.Add('"' + latin.ToString().Replace("\"", "\"\"") + '"');
        latin.Clear();
    }

    private static bool IsCjk(char character)
        => character is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

internal static class ArchiveSearchIndex
{
    public static void Upsert(SqliteConnection connection, SqliteTransaction? transaction, string recordType, string recordId, string content, string? sourceId, string? sessionId)
    {
        Remove(connection, transaction, recordType, [recordId]);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_search(content, source_id, record_type, record_id, session_id) VALUES ($content, $source, $type, $id, $session)";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", recordType);
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public static int Remove(SqliteConnection connection, SqliteTransaction? transaction, string recordType, IReadOnlyList<string> recordIds)
    {
        if (recordIds.Count == 0) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = recordIds.Select((_, index) => "$id" + index).ToArray();
        for (var index = 0; index < recordIds.Count; index++) command.Parameters.AddWithValue(parameters[index], recordIds[index]);
        command.Parameters.AddWithValue("$type", recordType);
        command.CommandText = "DELETE FROM memory_search WHERE record_type = $type AND record_id IN (" + string.Join(",", parameters) + ")";
        return command.ExecuteNonQuery();
    }
}
