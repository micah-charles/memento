namespace Memento.Core.Storage;

public sealed record ArchiveHealthReport(bool IntegrityOk, int SchemaVersion, int RecoverableAudioCount, int PendingConversationJobs, IReadOnlyList<string> Findings);

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
        return new ArchiveHealthReport(integrity, archive.CurrentSchemaVersion, recoverable, pending, findings);
    }
}
