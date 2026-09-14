using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed record ArchiveHealthReport(bool IntegrityOk, int SchemaVersion, int RecoverableAudioCount, int PendingConversationJobs, IReadOnlyList<string> Findings, int InvalidDerivedSpeechAssetCount = 0, int InvalidSourceAssetCount = 0, int InvalidSearchIndexCount = 0);

public static class ArchiveHealthCheck
{
    public static ArchiveHealthReport Run(SqliteArchive archive, string audioRootDirectory, DateTimeOffset? now = null)
    {
        var findings = new List<string>();
        var integrity = archive.IsIntegrityCheckClean();
        if (!integrity) findings.Add("SQLite integrity_check did not return ok.");
        var recoverable = Audio.AudioRecoveryScanner.Scan(audioRootDirectory).Count;
        if (recoverable > 0) findings.Add($"{recoverable} recoverable audio capture(s) require review.");
        var pending = 0;
        var clock = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var staleBefore = clock - TimeSpan.FromMinutes(5);
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM conversation_jobs WHERE (status = 'Pending' AND (next_attempt_at IS NULL OR next_attempt_at <= $now)) OR (status = 'Failed' AND next_attempt_at IS NOT NULL AND next_attempt_at <= $now) OR (status = 'Processing' AND updated_at <= $staleBefore)";
            command.Parameters.AddWithValue("$now", clock.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            pending = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (pending > 0) findings.Add($"{pending} conversation job(s) are due for processing.");
        var invalidSearchIndex = 0;
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            // Count semantic mismatches rather than only comparing row totals:
            // a corrupted index can retain the same number of rows while
            // losing content or Source-to-Session provenance.
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM (
                        SELECT record_type, record_id FROM memory_search
                        GROUP BY record_type, record_id HAVING COUNT(*) <> 1))
                  + (SELECT COUNT(*) FROM transcript_revisions r
                     LEFT JOIN sources s ON s.source_id = r.source_id
                     WHERE NOT EXISTS (
                         SELECT 1 FROM memory_search d
                         WHERE d.record_type = 'transcript_revision'
                           AND d.record_id = r.transcript_revision_id
                           AND d.content = r.text
                           AND COALESCE(d.source_id, '') = COALESCE(r.source_id, '')
                           AND COALESCE(d.session_id, '') = COALESCE(s.session_id, '')))
                  + (SELECT COUNT(*) FROM evidence_records e
                     WHERE NOT EXISTS (
                         SELECT 1 FROM memory_search d
                         WHERE d.record_type = 'evidence'
                           AND d.record_id = e.evidence_id
                           AND d.content = e.statement || ' ' || e.original_expression
                           AND COALESCE(d.source_id, '') = COALESCE(e.source_id, '')
                           AND COALESCE(d.session_id, '') = COALESCE(e.session_id, '')))
                  + (SELECT COUNT(*) FROM memory_claims c
                     WHERE NOT EXISTS (
                         SELECT 1 FROM memory_search d
                         WHERE d.record_type = 'memory_claim'
                           AND d.record_id = c.memory_claim_id
                           AND d.content = c.statement || ' ' || c.predicate || ' ' || c.object
                           AND COALESCE(d.source_id, '') = ''
                           AND COALESCE(d.session_id, '') = ''))
                  + (SELECT COUNT(*) FROM memory_search d
                     WHERE (d.record_type = 'transcript_revision' AND NOT EXISTS (SELECT 1 FROM transcript_revisions r WHERE r.transcript_revision_id = d.record_id))
                        OR (d.record_type = 'evidence' AND NOT EXISTS (SELECT 1 FROM evidence_records e WHERE e.evidence_id = d.record_id))
                        OR (d.record_type = 'memory_claim' AND NOT EXISTS (SELECT 1 FROM memory_claims c WHERE c.memory_claim_id = d.record_id))
                        OR d.record_type NOT IN ('transcript_revision', 'evidence', 'memory_claim'))
                """;
            invalidSearchIndex = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (invalidSearchIndex > 0) findings.Add($"Search index is missing or has {invalidSearchIndex} unexpected row(s); run ArchiveSearchService.Rebuild().");
        var invalidSources = 0;
        var invalidDerived = 0;
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file_path, byte_length, sha256 FROM sources WHERE recovery_status IN ('finalized', 'recovered') AND source_type = 'audio'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var expectedLength = reader.IsDBNull(1) ? -1 : reader.GetInt64(1);
                var expectedHash = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!IsFileMatching(path, expectedLength, expectedHash))
                    invalidSources++;
            }
        }

        if (invalidSources > 0) findings.Add($"{invalidSources} finalized or recovered source asset(s) failed integrity verification.");
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file_path, byte_length, sha256 FROM derived_speech_assets";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var expectedLength = reader.GetInt64(1);
                var expectedHash = reader.GetString(2);
                if (!IsFileMatching(path, expectedLength, expectedHash))
                {
                    invalidDerived++;
                }
            }
        }

        if (invalidDerived > 0) findings.Add($"{invalidDerived} derived speech asset(s) failed integrity verification.");
        return new ArchiveHealthReport(integrity, archive.CurrentSchemaVersion, recoverable, pending, findings, invalidDerived, invalidSources, invalidSearchIndex);
    }

    private static bool IsFileMatching(string path, long expectedLength, string? expectedHash)
    {
        if (!File.Exists(path)) return false;
        var fileLength = new FileInfo(path).Length;
        if (expectedLength >= 0 && fileLength != expectedLength) return false;
        if (string.IsNullOrWhiteSpace(expectedHash)) return true;
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
