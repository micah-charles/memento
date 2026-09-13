using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed record ArchiveExportResult(string ExportDirectory, string ManifestPath, IReadOnlyDictionary<string, string> FileHashes);

public static class ArchiveExporter
{
    private static readonly string[] Tables = ["sessions", "turns", "consent_events", "sources", "provider_interactions", "transcript_revisions", "clarification_events", "vocabulary_entries", "conversation_jobs", "evidence_records", "memory_claims", "evidence_claim_links", "person_entities", "entity_aliases", "evidence_entity_links", "review_annotations", "response_episodes", "derived_speech_assets", "deletion_tombstones"];

    public static ArchiveExportResult Export(SqliteArchive archive, string destinationDirectory, bool includeMedia = false, bool includeWithdrawn = false)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("An export directory is required.", nameof(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        var exportDirectory = Path.Combine(destinationDirectory, "memento-export-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = archive.OpenConnection();
        foreach (var table in Tables)
        {
            var relative = table + ".jsonl";
            var path = Path.Combine(exportDirectory, relative);
            WriteTable(connection, table, path, includeWithdrawn);
            hashes[relative] = Hash(path);
        }

        var backupPath = Path.Combine(exportDirectory, "archive.sqlite");
        using (var backup = connection.CreateCommand())
        {
            backup.CommandText = "VACUUM INTO $path";
            backup.Parameters.AddWithValue("$path", backupPath);
            backup.ExecuteNonQuery();
        }
        if (!includeWithdrawn)
            SanitizeWithdrawnRecords(backupPath);
        hashes["archive.sqlite"] = Hash(backupPath);

        if (includeMedia)
        {
            var mediaDirectory = Path.Combine(exportDirectory, "media");
            Directory.CreateDirectory(mediaDirectory);
            using (var sources = connection.CreateCommand())
            {
                sources.CommandText = includeWithdrawn
                    ? "SELECT source_id, file_path FROM sources WHERE file_path IS NOT NULL"
                    : "SELECT source_id, file_path FROM sources WHERE file_path IS NOT NULL AND recovery_status <> 'withdrawn'";
                using var reader = sources.ExecuteReader();
                while (reader.Read())
                {
                    var sourceId = reader.GetString(0);
                    var sourcePath = reader.GetString(1);
                    if (!File.Exists(sourcePath)) continue;
                    var filename = Path.GetFileName(sourcePath);
                    if (string.IsNullOrWhiteSpace(filename)) continue;
                    var target = GetUniqueMediaPath(mediaDirectory, filename, "source-" + sourceId);
                    File.Copy(sourcePath, target, overwrite: false);
                    hashes[Path.Combine("media", Path.GetFileName(target)).Replace('\\', '/')] = Hash(target);
                }
            }

            using (var derived = connection.CreateCommand())
            {
                derived.CommandText = includeWithdrawn
                    ? "SELECT derived_speech_asset_id, file_path FROM derived_speech_assets"
                    : "SELECT d.derived_speech_asset_id, d.file_path FROM derived_speech_assets d WHERE NOT EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = d.turn_id) OR (ws.turn_id IS NULL AND d.turn_id IS NULL AND ws.session_id = d.session_id)))";
                using var reader = derived.ExecuteReader();
                while (reader.Read())
                {
                    var assetId = reader.GetString(0);
                    var sourcePath = reader.GetString(1);
                    if (!File.Exists(sourcePath)) continue;
                    var filename = Path.GetFileName(sourcePath);
                    if (string.IsNullOrWhiteSpace(filename)) continue;
                    var exportName = "derived-" + filename;
                    var target = GetUniqueMediaPath(mediaDirectory, exportName, "derived-" + assetId);
                    File.Copy(sourcePath, target, overwrite: false);
                    hashes[Path.Combine("media", Path.GetFileName(target)).Replace('\\', '/')] = Hash(target);
                }
            }
        }

        var manifestPath = Path.Combine(exportDirectory, "manifest.json");
        var manifest = new { schema_version = archive.CurrentSchemaVersion, exported_at = DateTimeOffset.UtcNow, files = hashes.OrderBy(item => item.Key).Select(item => new { path = item.Key, sha256 = item.Value }).ToArray() };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        hashes["manifest.json"] = Hash(manifestPath);
        return new ArchiveExportResult(exportDirectory, manifestPath, hashes);
    }

    private static string GetUniqueMediaPath(string mediaDirectory, string preferredName, string idPrefix)
    {
        var target = Path.Combine(mediaDirectory, preferredName);
        if (!File.Exists(target)) return target;
        var safePrefix = string.Concat(idPrefix.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var candidate = Path.Combine(mediaDirectory, safePrefix + "-" + preferredName);
        var suffix = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(mediaDirectory, safePrefix + "-" + suffix++ + "-" + preferredName);
        return candidate;
    }

    private static void WriteTable(SqliteConnection connection, string table, string path, bool includeWithdrawn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectTableSql(table, includeWithdrawn);
        using var reader = command.ExecuteReader();
        using var writer = new StreamWriter(path, false);
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            writer.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    private static string SelectTableSql(string table, bool includeWithdrawn)
    {
        if (includeWithdrawn) return $"SELECT * FROM {table}";
        const string withdrawn = "(SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')";
        const string withdrawnEvidence = "(SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))";
        const string withdrawnClaims = "(SELECT DISTINCT l.memory_claim_id FROM evidence_claim_links l JOIN evidence_records e ON e.evidence_id = l.evidence_id JOIN sources s ON s.source_id = e.source_id WHERE s.recovery_status = 'withdrawn')";
        const string withdrawnClarifications = "(SELECT clarification_event_id FROM clarification_events WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))";
        return table switch
        {
            "sources" => "SELECT * FROM sources WHERE recovery_status <> 'withdrawn'",
            "transcript_revisions" => $"SELECT * FROM transcript_revisions WHERE source_id NOT IN {withdrawn}",
            "clarification_events" => $"SELECT * FROM clarification_events WHERE source_id NOT IN {withdrawn}",
            "vocabulary_entries" => $"SELECT * FROM vocabulary_entries WHERE source_clarification_event_id NOT IN {withdrawnClarifications}",
            "conversation_jobs" => $"SELECT * FROM conversation_jobs WHERE source_id NOT IN {withdrawn}",
            "evidence_records" => $"SELECT * FROM evidence_records WHERE source_id NOT IN {withdrawn}",
            "evidence_claim_links" => $"SELECT * FROM evidence_claim_links WHERE evidence_id NOT IN {withdrawnEvidence} AND memory_claim_id NOT IN {withdrawnClaims}",
            "evidence_entity_links" => $"SELECT * FROM evidence_entity_links WHERE evidence_id NOT IN {withdrawnEvidence}",
            "response_episodes" => $"SELECT * FROM response_episodes WHERE stimulus_evidence_id NOT IN {withdrawnEvidence} AND response_evidence_id NOT IN {withdrawnEvidence} AND (follow_up_evidence_id IS NULL OR follow_up_evidence_id NOT IN {withdrawnEvidence})",
            "memory_claims" => $"SELECT * FROM memory_claims WHERE memory_claim_id NOT IN {withdrawnClaims}",
            "provider_interactions" => "SELECT p.* FROM provider_interactions p WHERE NOT EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = p.turn_id) OR (ws.turn_id IS NULL AND p.turn_id IS NULL AND ws.session_id = p.session_id)))",
            "derived_speech_assets" => "SELECT d.* FROM derived_speech_assets d WHERE NOT EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = d.turn_id) OR (ws.turn_id IS NULL AND d.turn_id IS NULL AND ws.session_id = d.session_id)))",
            "entity_aliases" => $"SELECT * FROM entity_aliases WHERE source_clarification_event_id IS NULL OR source_clarification_event_id NOT IN {withdrawnClarifications}",
            "review_annotations" => $"SELECT * FROM review_annotations WHERE NOT ((target_type = 'source' AND target_id IN {withdrawn} AND annotation_type <> 'withdrawal') OR (target_type = 'transcript_revision' AND target_id IN (SELECT transcript_revision_id FROM transcript_revisions WHERE source_id IN {withdrawn})) OR (target_type = 'evidence' AND target_id IN {withdrawnEvidence}) OR (target_type = 'memory_claim' AND target_id IN {withdrawnClaims}))",
            _ => $"SELECT * FROM {table}"
        };
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void SanitizeWithdrawnRecords(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys = ON";
        foreignKeys.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        ExecuteSanitize(connection, transaction, "CREATE TEMP TABLE withdrawn_memory_claims(memory_claim_id TEXT PRIMARY KEY)");
        ExecuteSanitize(connection, transaction, "INSERT INTO withdrawn_memory_claims(memory_claim_id) SELECT DISTINCT l.memory_claim_id FROM evidence_claim_links l JOIN evidence_records e ON e.evidence_id = l.evidence_id JOIN sources s ON s.source_id = e.source_id WHERE s.recovery_status = 'withdrawn'");
        ExecuteSanitize(connection, transaction, "DELETE FROM review_annotations WHERE (target_type = 'source' AND target_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn') AND annotation_type <> 'withdrawal') OR (target_type = 'transcript_revision' AND target_id IN (SELECT transcript_revision_id FROM transcript_revisions WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))) OR (target_type = 'evidence' AND target_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))) OR (target_type = 'memory_claim' AND target_id IN (SELECT memory_claim_id FROM withdrawn_memory_claims))");
        ExecuteSanitize(connection, transaction, "DELETE FROM response_episodes WHERE stimulus_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')) OR response_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')) OR follow_up_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))");
        ExecuteSanitize(connection, transaction, "DELETE FROM evidence_entity_links WHERE evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))");
        ExecuteSanitize(connection, transaction, "DELETE FROM memory_search WHERE (record_type IN ('transcript_revision', 'evidence') AND source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')) OR (record_type = 'memory_claim' AND record_id IN (SELECT memory_claim_id FROM withdrawn_memory_claims))");
        ExecuteSanitize(connection, transaction, "DELETE FROM evidence_claim_links WHERE evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')) OR memory_claim_id IN (SELECT memory_claim_id FROM withdrawn_memory_claims)");
        ExecuteSanitize(connection, transaction, "DELETE FROM vocabulary_entries WHERE source_clarification_event_id IN (SELECT clarification_event_id FROM clarification_events WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))");
        ExecuteSanitize(connection, transaction, "DELETE FROM entity_aliases WHERE source_clarification_event_id IN (SELECT clarification_event_id FROM clarification_events WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn'))");
        ExecuteSanitize(connection, transaction, "DELETE FROM clarification_events WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')");
        ExecuteSanitize(connection, transaction, "DELETE FROM conversation_jobs WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')");
        ExecuteSanitize(connection, transaction, "DELETE FROM provider_interactions WHERE EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = provider_interactions.turn_id) OR (ws.turn_id IS NULL AND provider_interactions.turn_id IS NULL AND ws.session_id = provider_interactions.session_id)))");
        ExecuteSanitize(connection, transaction, "DELETE FROM derived_speech_assets WHERE EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = derived_speech_assets.turn_id) OR (ws.turn_id IS NULL AND derived_speech_assets.turn_id IS NULL AND ws.session_id = derived_speech_assets.session_id)))");
        ExecuteSanitize(connection, transaction, "DELETE FROM evidence_records WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')");
        ExecuteSanitize(connection, transaction, "DELETE FROM memory_claims WHERE memory_claim_id IN (SELECT memory_claim_id FROM withdrawn_memory_claims)");
        ExecuteSanitize(connection, transaction, "DELETE FROM transcript_revisions WHERE source_id IN (SELECT source_id FROM sources WHERE recovery_status = 'withdrawn')");
        ExecuteSanitize(connection, transaction, "DELETE FROM sources WHERE recovery_status = 'withdrawn'");
        transaction.Commit();
    }

    private static void ExecuteSanitize(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public static class ArchiveBackupProtector
{
    private static readonly byte[] Magic = "MEMENTO1"u8.ToArray();

    public static void EncryptFile(string sourcePath, string destinationPath, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A backup password is required.", nameof(password));
        var plain = File.ReadAllBytes(sourcePath);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        WriteAtomically(destinationPath, stream =>
        {
            stream.Write(Magic); stream.Write(salt); stream.Write(nonce); stream.Write(tag); stream.Write(cipher);
        });
    }

    public static void DecryptFile(string sourcePath, string destinationPath, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A backup password is required.", nameof(password));
        var payload = File.ReadAllBytes(sourcePath);
        if (payload.Length < Magic.Length + 16 + 12 + 16 || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Unsupported MEMENTO backup.");
        var salt = payload.AsSpan(Magic.Length, 16).ToArray();
        var nonce = payload.AsSpan(Magic.Length + 16, 12).ToArray();
        var tag = payload.AsSpan(Magic.Length + 28, 16).ToArray();
        var cipher = payload.AsSpan(Magic.Length + 44).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        WriteAtomically(destinationPath, stream => stream.Write(plain));
    }

    public static void ReencryptFile(string sourcePath, string destinationPath, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword)) throw new ArgumentException("The existing backup password is required.", nameof(oldPassword));
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("The new backup password is required.", nameof(newPassword));
        var temporaryPlaintext = Path.Combine(Path.GetTempPath(), "memento-rekey-" + Guid.NewGuid().ToString("N"));
        try
        {
            DecryptFile(sourcePath, temporaryPlaintext, oldPassword);
            EncryptFile(temporaryPlaintext, destinationPath, newPassword);
        }
        finally
        {
            if (File.Exists(temporaryPlaintext)) File.Delete(temporaryPlaintext);
        }
    }

    public static void EncryptDirectory(string sourceDirectory, string destinationPath, string password)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        var temporaryZip = Path.Combine(Path.GetTempPath(), "memento-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            ZipFile.CreateFromDirectory(sourceDirectory, temporaryZip, CompressionLevel.Fastest, includeBaseDirectory: false);
            EncryptFile(temporaryZip, destinationPath, password);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
        }
    }

    public static ArchiveRestoreResult DecryptDirectory(string sourcePath, string destinationDirectory, string password)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("A restore directory is required.", nameof(destinationDirectory));
        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The restore directory must be empty.");
        Directory.CreateDirectory(destination);
        var temporaryZip = Path.Combine(Path.GetTempPath(), "memento-restore-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DecryptFile(sourcePath, temporaryZip, password);
            ExtractZipSafely(temporaryZip, destination);
            return VerifyManifest(destination);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
        }
    }

    public static ArchiveRestoreResult ReencryptDirectory(string sourcePath, string destinationPath, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword)) throw new ArgumentException("The existing backup password is required.", nameof(oldPassword));
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("The new backup password is required.", nameof(newPassword));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "memento-rekey-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = DecryptDirectory(sourcePath, temporaryDirectory, oldPassword);
            if (!report.IntegrityOk)
                throw new InvalidDataException("The existing backup failed manifest verification and cannot be re-encrypted.");
            EncryptDirectory(temporaryDirectory, destinationPath, newPassword);
            return report;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void ExtractZipSafely(string zipPath, string destinationDirectory)
    {
        var root = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (parent is not null) Directory.CreateDirectory(parent);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    private static void WriteAtomically(string destinationPath, Action<FileStream> write)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (directory is not null) Directory.CreateDirectory(directory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ArchiveRestoreResult VerifyManifest(string restoreDirectory)
    {
        var findings = new List<string>();
        var manifestPath = Path.Combine(restoreDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return new ArchiveRestoreResult(false, ["Backup manifest.json is missing."]);

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return new ArchiveRestoreResult(false, ["Backup manifest has no files list."]);

        var root = Path.GetFullPath(restoreDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files.EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString() ?? string.Empty;
            var expected = file.GetProperty("sha256").GetString() ?? string.Empty;
            var candidate = Path.GetFullPath(Path.Combine(restoreDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            {
                findings.Add($"Missing backup file: {relative}");
                continue;
            }

            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                findings.Add($"Backup hash mismatch: {relative}");
        }

        return new ArchiveRestoreResult(findings.Count == 0, findings);
    }
}

public sealed record ArchiveRestoreResult(bool IntegrityOk, IReadOnlyList<string> Findings);
