using System.Security.Cryptography;

namespace Memento.Core.Storage;

public sealed record ArchiveHealthReport(bool IntegrityOk, int SchemaVersion, int RecoverableAudioCount, int PendingConversationJobs, IReadOnlyList<string> Findings, int InvalidDerivedSpeechAssetCount = 0, int InvalidSourceAssetCount = 0);

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
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM conversation_jobs WHERE status IN ('Pending', 'Processing', 'Failed') AND (next_attempt_at IS NULL OR next_attempt_at <= $now)";
            command.Parameters.AddWithValue("$now", (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            pending = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (pending > 0) findings.Add($"{pending} conversation job(s) are due for processing.");
        var invalidSources = 0;
        var invalidDerived = 0;
        using (var connection = archive.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT file_path, byte_length, sha256 FROM sources WHERE recovery_status = 'finalized' AND source_type = 'audio'";
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

        if (invalidSources > 0) findings.Add($"{invalidSources} finalized source asset(s) failed integrity verification.");
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
        return new ArchiveHealthReport(integrity, archive.CurrentSchemaVersion, recoverable, pending, findings, invalidDerived, invalidSources);
    }

    private static bool IsFileMatching(string path, long expectedLength, string? expectedHash)
    {
        if (!File.Exists(path)) return false;
        var bytes = File.ReadAllBytes(path);
        if (expectedLength >= 0 && bytes.LongLength != expectedLength) return false;
        if (string.IsNullOrWhiteSpace(expectedHash)) return true;
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
