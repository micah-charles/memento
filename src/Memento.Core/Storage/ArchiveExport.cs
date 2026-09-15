using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed record ArchiveExportResult(string ExportDirectory, string ManifestPath, IReadOnlyDictionary<string, string> FileHashes);

public sealed record ScopedArchiveExportResult(
    string ExportDirectory,
    string ManifestPath,
    IReadOnlyDictionary<string, string> FileHashes,
    IReadOnlyList<string> ExportedClaimIds,
    IReadOnlyList<string> ExportedEvidenceIds);

public static class ArchiveExporter
{
    private static readonly string[] Tables = ["sessions", "turns", "consent_events", "sources", "provider_interactions", "transcript_revisions", "clarification_events", "vocabulary_entries", "conversation_jobs", "evidence_records", "memory_claims", "evidence_claim_links", "person_entities", "entity_aliases", "evidence_entity_links", "review_annotations", "response_episodes", "derived_speech_assets", "deletion_tombstones", "companion_sessions", "companion_chunks", "companion_messages", "companion_text_versions", "companion_spans", "companion_playback"];

    public static ArchiveExportResult Export(SqliteArchive archive, string destinationDirectory, bool includeMedia = false, bool includeWithdrawn = false)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("An export directory is required.", nameof(destinationDirectory));
        ArchivePathSafety.EnsureNoReparsePointInPath(destinationDirectory, "The export directory");
        var archiveRoot = ArchivePathSafety.GetArchiveRoot(archive);
        Directory.CreateDirectory(destinationDirectory);
        var exportDirectory = Path.Combine(destinationDirectory, "memento-export-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var backupPath = Path.Combine(exportDirectory, "archive.sqlite");
            // Take the canonical SQLite snapshot before reading any export rows.
            // JSONL and the database must describe one archive state even when a
            // retry worker is writing to the live archive concurrently.
            using (var connection = archive.OpenConnection())
            {
                using var backup = connection.CreateCommand();
                backup.CommandText = "VACUUM INTO $path";
                backup.Parameters.AddWithValue("$path", backupPath);
                backup.ExecuteNonQuery();
            }
            if (!includeWithdrawn)
                SanitizeWithdrawnRecords(backupPath);
            hashes["archive.sqlite"] = Hash(backupPath);

            using var snapshot = OpenSnapshotConnection(backupPath);
            foreach (var table in Tables)
            {
                var relative = table + ".jsonl";
                var path = Path.Combine(exportDirectory, relative);
                WriteTable(snapshot, table, path, includeWithdrawn);
                hashes[relative] = Hash(path);
            }

            if (includeMedia)
            {
                var mediaDirectory = Path.Combine(exportDirectory, "media");
                Directory.CreateDirectory(mediaDirectory);
                using (var sources = snapshot.CreateCommand())
                {
                    sources.CommandText = includeWithdrawn
                        ? "SELECT source_id, file_path, byte_length, sha256 FROM sources WHERE file_path IS NOT NULL"
                        : "SELECT source_id, file_path, byte_length, sha256 FROM sources WHERE file_path IS NOT NULL AND recovery_status <> 'withdrawn'";
                    using var reader = sources.ExecuteReader();
                    while (reader.Read())
                    {
                        var sourceId = reader.GetString(0);
                        var sourcePath = reader.GetString(1);
                        var expectedLength = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                        var expectedHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var target = GetUniqueMediaPath(mediaDirectory, RequireMediaPath(sourceId, sourcePath, archiveRoot), "source-" + sourceId);
                        var actualHash = CopyVerifiedMedia(sourceId, sourcePath, target, expectedLength, expectedHash);
                        hashes[Path.Combine("media", Path.GetFileName(target)).Replace('\\', '/')] = actualHash;
                    }
                }

                using (var derived = snapshot.CreateCommand())
                {
                    derived.CommandText = includeWithdrawn
                        ? "SELECT derived_speech_asset_id, file_path, byte_length, sha256 FROM derived_speech_assets"
                        : "SELECT d.derived_speech_asset_id, d.file_path, d.byte_length, d.sha256 FROM derived_speech_assets d WHERE NOT EXISTS (SELECT 1 FROM sources ws WHERE ws.recovery_status = 'withdrawn' AND ((ws.turn_id IS NOT NULL AND ws.turn_id = d.turn_id) OR (ws.turn_id IS NULL AND d.turn_id IS NULL AND ws.session_id = d.session_id)))";
                    using var reader = derived.ExecuteReader();
                    while (reader.Read())
                    {
                        var assetId = reader.GetString(0);
                        var sourcePath = reader.GetString(1);
                        var expectedLength = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                        var expectedHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                        var filename = RequireMediaPath(assetId, sourcePath, archiveRoot);
                        var target = GetUniqueMediaPath(mediaDirectory, "derived-" + filename, "derived-" + assetId);
                        var actualHash = CopyVerifiedMedia("derived asset " + assetId, sourcePath, target, expectedLength, expectedHash);
                        hashes[Path.Combine("media", Path.GetFileName(target)).Replace('\\', '/')] = actualHash;
                    }
                }
            }

            var manifestPath = Path.Combine(exportDirectory, "manifest.json");
            var manifest = new { schema_version = archive.CurrentSchemaVersion, exported_at = DateTimeOffset.UtcNow, files = hashes.OrderBy(item => item.Key).Select(item => new { path = item.Key, sha256 = item.Value }).ToArray() };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            hashes["manifest.json"] = Hash(manifestPath);
            return new ArchiveExportResult(exportDirectory, manifestPath, hashes);
        }
        catch
        {
            try
            {
                if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, recursive: true);
            }
            catch
            {
                // Preserve the original export error if cleanup itself fails.
            }
            throw;
        }
    }

    /// <summary>
    /// Writes a deliberately narrow, shareable export for selected Memory Claims.
    /// The output never contains the SQLite archive, Source paths, audio, transcript
    /// text, provider payloads, or search indexes. Evidence remains attributable by
    /// opaque IDs and authority metadata, while its content and Source link are
    /// explicitly labelled as withheld.
    /// </summary>
    public static ScopedArchiveExportResult ExportRedacted(
        SqliteArchive archive,
        string destinationDirectory,
        IReadOnlyCollection<string> memoryClaimIds,
        bool includeWithdrawn = false)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("An export directory is required.", nameof(destinationDirectory));
        if (memoryClaimIds is null || memoryClaimIds.Count == 0) throw new ArgumentException("At least one Memory Claim ID is required.", nameof(memoryClaimIds));
        ArchivePathSafety.EnsureNoReparsePointInPath(destinationDirectory, "The export directory");

        var requestedClaimIds = memoryClaimIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedClaimIds.Length != memoryClaimIds.Count)
            throw new ArgumentException("Memory Claim IDs must be non-empty and unique.", nameof(memoryClaimIds));

        Directory.CreateDirectory(destinationDirectory);
        var exportDirectory = Path.Combine(destinationDirectory, "memento-scoped-export-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportDirectory);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var exportedClaimIds = new List<string>();
        var exportedEvidenceIds = new List<string>();
        var snapshotPath = Path.Combine(exportDirectory, ".snapshot.sqlite");
        SqliteConnection? snapshot = null;
        try
        {
            // Take a consistent snapshot, but never publish the snapshot itself.
            using (var connection = archive.OpenConnection())
            using (var backup = connection.CreateCommand())
            {
                backup.CommandText = "VACUUM INTO $path";
                backup.Parameters.AddWithValue("$path", snapshotPath);
                backup.ExecuteNonQuery();
            }

            snapshot = OpenSnapshotConnection(snapshotPath);
            var withdrawnClaimFilter = includeWithdrawn
                ? string.Empty
                : " AND NOT EXISTS (SELECT 1 FROM evidence_claim_links wl JOIN evidence_records we ON we.evidence_id = wl.evidence_id JOIN sources ws ON ws.source_id = we.source_id WHERE wl.memory_claim_id = c.memory_claim_id AND ws.recovery_status = 'withdrawn')";

            var claimsPath = Path.Combine(exportDirectory, "memory_claims.jsonl");
            using (var command = snapshot.CreateCommand())
            {
                var claimParameters = AddIdParameters(command, requestedClaimIds);
                var claimFilter = string.Join(", ", claimParameters.Select(parameter => parameter.ParameterName));
                command.CommandText = $"SELECT c.memory_claim_id, c.statement, c.subject_person_id, c.predicate, c.object, c.status, c.created_at FROM memory_claims c WHERE c.status = 'Reviewed' AND c.memory_claim_id IN ({claimFilter}){withdrawnClaimFilter} ORDER BY c.created_at, c.memory_claim_id";
                foreach (var parameter in claimParameters) command.Parameters.Add(parameter);
                using var reader = command.ExecuteReader();
                using var writer = new StreamWriter(claimsPath, false);
                while (reader.Read())
                {
                    var claimId = reader.GetString(0);
                    exportedClaimIds.Add(claimId);
                    WriteJsonLine(writer, new
                    {
                        memory_claim_id = claimId,
                        statement = reader.GetString(1),
                        subject_person_id = reader.IsDBNull(2) ? null : reader.GetString(2),
                        predicate = reader.GetString(3),
                        @object = reader.GetString(4),
                        status = reader.GetString(5),
                        created_at = reader.GetString(6)
                    });
                }
            }

            if (exportedClaimIds.Count != requestedClaimIds.Length)
                throw new InvalidDataException("One or more selected Memory Claims are missing, withdrawn, or not reviewed.");
            hashes["memory_claims.jsonl"] = Hash(claimsPath);

            var evidencePath = Path.Combine(exportDirectory, "evidence.jsonl");
            var linkPath = Path.Combine(exportDirectory, "claim_evidence_links.jsonl");
            var annotationPath = Path.Combine(exportDirectory, "annotations.jsonl");
            var exportedEvidence = new HashSet<string>(StringComparer.Ordinal);
            using (var evidenceWriter = new StreamWriter(evidencePath, false))
            using (var linkWriter = new StreamWriter(linkPath, false))
            using (var command = snapshot.CreateCommand())
            {
                var includedClaimParameters = AddIdParameters(command, exportedClaimIds);
                var evidenceWithdrawnFilter = includeWithdrawn ? string.Empty : " AND s.recovery_status <> 'withdrawn'";
                command.CommandText = $"SELECT e.evidence_id, e.kind, e.participant_certainty, e.speaker_confirmed, e.created_at, l.memory_claim_id, l.relationship, l.created_at FROM evidence_claim_links l JOIN evidence_records e ON e.evidence_id = l.evidence_id JOIN sources s ON s.source_id = e.source_id WHERE l.memory_claim_id IN ({string.Join(", ", includedClaimParameters.Select(parameter => parameter.ParameterName))}){evidenceWithdrawnFilter} ORDER BY e.created_at, e.evidence_id, l.memory_claim_id, l.relationship";
                foreach (var parameter in includedClaimParameters) command.Parameters.Add(parameter);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var evidenceId = reader.GetString(0);
                    if (exportedEvidence.Add(evidenceId))
                    {
                        exportedEvidenceIds.Add(evidenceId);
                        WriteJsonLine(evidenceWriter, new
                        {
                            evidence_id = evidenceId,
                            kind = reader.GetString(1),
                            statement = "[REDACTED]",
                            original_expression = "[REDACTED]",
                            participant_certainty = reader.GetString(2),
                            speaker_confirmed = reader.GetInt64(3) != 0,
                            created_at = reader.GetString(4),
                            source_withheld = true,
                            transcript_withheld = true,
                            audio_withheld = true
                        });
                    }

                    WriteJsonLine(linkWriter, new
                    {
                        evidence_id = evidenceId,
                        memory_claim_id = reader.GetString(5),
                        relationship = reader.GetString(6),
                        created_at = reader.GetString(7),
                        source_reference = "withheld"
                    });
                }
            }
            hashes["evidence.jsonl"] = Hash(evidencePath);
            hashes["claim_evidence_links.jsonl"] = Hash(linkPath);

            using (var command = snapshot.CreateCommand())
            {
                var ids = exportedClaimIds.Concat(exportedEvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
                var parameters = AddIdParameters(command, ids);
                command.CommandText = $"SELECT annotation_id, target_type, target_id, actor_id, annotation_type, assessment, created_at FROM review_annotations WHERE target_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))}) ORDER BY created_at, annotation_id";
                foreach (var parameter in parameters) command.Parameters.Add(parameter);
                using var reader = command.ExecuteReader();
                using var writer = new StreamWriter(annotationPath, false);
                while (reader.Read())
                {
                    WriteJsonLine(writer, new
                    {
                        annotation_id = reader.GetString(0),
                        target_type = reader.GetString(1),
                        target_id = reader.GetString(2),
                        actor_id = reader.GetString(3),
                        annotation_type = reader.GetString(4),
                        body = "[REDACTED]",
                        assessment = reader.IsDBNull(5) ? null : reader.GetString(5),
                        created_at = reader.GetString(6),
                        content_withheld = true
                    });
                }
            }
            hashes["annotations.jsonl"] = Hash(annotationPath);

            // The temporary snapshot is deliberately never part of the export.
            snapshot.Dispose();
            snapshot = null;
            File.Delete(snapshotPath);

            var manifestPath = Path.Combine(exportDirectory, "manifest.json");
            var manifest = new
            {
                schema_version = archive.CurrentSchemaVersion,
                export_type = "scoped-redacted",
                exported_at = DateTimeOffset.UtcNow,
                scope = new
                {
                    memory_claim_ids = exportedClaimIds,
                    source_audio_included = false,
                    source_reference = "withheld",
                    transcript_content_included = false,
                    provider_interactions_included = false,
                    redaction_notice = "Evidence content, transcript links, Source paths, audio, and provider payloads are withheld."
                },
                files = hashes.OrderBy(item => item.Key).Select(item => new { path = item.Key, sha256 = item.Value }).ToArray()
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            hashes["manifest.json"] = Hash(manifestPath);
            return new ScopedArchiveExportResult(exportDirectory, manifestPath, hashes, exportedClaimIds, exportedEvidenceIds);
        }
        catch
        {
            try
            {
                snapshot?.Dispose();
                if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, recursive: true);
            }
            catch
            {
                // Preserve the original export error if cleanup itself fails.
            }
            throw;
        }
    }

    private static List<SqliteParameter> AddIdParameters(SqliteCommand command, IReadOnlyCollection<string> ids)
    {
        var parameters = new List<SqliteParameter>(ids.Count);
        var index = 0;
        foreach (var id in ids)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id" + index++;
            parameter.Value = id;
            parameters.Add(parameter);
        }
        return parameters;
    }

    private static void WriteJsonLine(StreamWriter writer, object value)
        => writer.WriteLine(JsonSerializer.Serialize(value));

    private static string RequireMediaPath(string recordId, string sourcePath, string archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new FileNotFoundException($"Media for {recordId} has no file path.", sourcePath);
        if (!ArchivePathSafety.IsPathUnderRoot(sourcePath, archiveRoot))
            throw new InvalidDataException($"Media for {recordId} is outside the archive directory.");
        ArchivePathSafety.EnsureNoReparsePointInPath(sourcePath, "The export media path");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Media for {recordId} is missing.", sourcePath);
        var filename = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidDataException($"Media for {recordId} has no usable file name.");
        return filename;
    }

    private static string CopyVerifiedMedia(string recordId, string sourcePath, string targetPath, long? expectedLength, string? expectedHash)
    {
        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
            var actualLength = new FileInfo(targetPath).Length;
            if (expectedLength is long length && length >= 0 && actualLength != length)
                throw new InvalidDataException($"Media for {recordId} changed length during export.");
            var actualHash = Hash(targetPath);
            if (IsSha256(expectedHash) && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Media for {recordId} failed its recorded SHA-256 integrity check.");
            return actualHash;
        }
        catch
        {
            try
            {
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            catch
            {
                // Preserve the original media verification error.
            }
            throw;
        }
    }

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static SqliteConnection OpenSnapshotConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
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

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

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
        ExecuteSanitize(connection, transaction, "DELETE FROM companion_messages WHERE session_id IN (SELECT session_id FROM companion_sessions WHERE blocked=1)");
        ExecuteSanitize(connection, transaction, "DELETE FROM derived_speech_assets WHERE session_id IN (SELECT session_id FROM companion_sessions WHERE blocked=1)");
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
    private static readonly byte[] LegacyMagic = "MEMENTO1"u8.ToArray();
    private static readonly byte[] StreamingMagic = "MEMENTO2"u8.ToArray();
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int ChunkSize = 1024 * 1024;

    public static void EncryptFile(string sourcePath, string destinationPath, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A backup password is required.", nameof(password));
        EnsureDistinctPaths(sourcePath, destinationPath, "The backup source and destination must differ.");
        EnsureNoReparsePointInPath(sourcePath, "The backup input path");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
        WriteAtomically(destinationPath, stream =>
        {
            stream.Write(StreamingMagic);
            stream.Write(salt);
            EncryptChunks(input, stream, key);
        });
    }

    public static void DecryptFile(string sourcePath, string destinationPath, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("A backup password is required.", nameof(password));
        EnsureDistinctPaths(sourcePath, destinationPath, "The backup source and destination must differ.");
        EnsureNoReparsePointInPath(sourcePath, "The backup input path");
        var magic = new byte[StreamingMagic.Length];
        using (var header = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
        {
            try { header.ReadExactly(magic); }
            catch (EndOfStreamException) { throw new InvalidDataException("Unsupported MEMENTO backup."); }
        }

        if (magic.AsSpan().SequenceEqual(LegacyMagic))
        {
            DecryptLegacyFile(sourcePath, destinationPath, password);
            return;
        }
        if (!magic.AsSpan().SequenceEqual(StreamingMagic))
            throw new InvalidDataException("Unsupported MEMENTO backup.");

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        input.Position = StreamingMagic.Length;
        var salt = new byte[SaltLength];
        try { input.ReadExactly(salt); }
        catch (EndOfStreamException) { throw new InvalidDataException("MEMENTO backup header is incomplete."); }
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
        WriteAtomically(destinationPath, stream => DecryptChunks(input, stream, key));
    }

    private static void EncryptChunks(Stream input, Stream output, byte[] key)
    {
        using var aes = new AesGcm(key, TagLength);
        var buffer = new byte[ChunkSize];
        long chunkIndex = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var cipher = new byte[read];
            var tag = new byte[TagLength];
            var associatedData = AssociatedData(chunkIndex, read);
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag, associatedData);
            WriteInt32(output, read);
            output.Write(nonce);
            output.Write(tag);
            output.Write(cipher);
            chunkIndex++;
        }

        WriteInt32(output, 0);
    }

    private static void DecryptChunks(Stream input, Stream output, byte[] key)
    {
        using var aes = new AesGcm(key, TagLength);
        var lengthBuffer = new byte[sizeof(int)];
        var nonce = new byte[NonceLength];
        var tag = new byte[TagLength];
        long chunkIndex = 0;
        while (true)
        {
            try { input.ReadExactly(lengthBuffer); }
            catch (EndOfStreamException) { throw new InvalidDataException("MEMENTO backup chunk header is incomplete."); }
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (length == 0)
            {
                if (input.Position != input.Length)
                    throw new InvalidDataException("MEMENTO backup contains data after its final chunk.");
                return;
            }
            if (length < 0 || length > ChunkSize)
                throw new InvalidDataException("MEMENTO backup chunk length is invalid.");

            try
            {
                input.ReadExactly(nonce);
                input.ReadExactly(tag);
                var cipher = new byte[length];
                input.ReadExactly(cipher);
                var plain = new byte[length];
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(chunkIndex, length));
                output.Write(plain);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("MEMENTO backup chunk is truncated.");
            }
            catch (CryptographicException error)
            {
                throw new InvalidDataException("MEMENTO backup authentication failed.", error);
            }

            chunkIndex++;
        }
    }

    private static void DecryptLegacyFile(string sourcePath, string destinationPath, string password)
    {
        var payload = File.ReadAllBytes(sourcePath);
        if (payload.Length < LegacyMagic.Length + SaltLength + NonceLength + TagLength || !payload.AsSpan(0, LegacyMagic.Length).SequenceEqual(LegacyMagic))
            throw new InvalidDataException("Unsupported MEMENTO backup.");
        var salt = payload.AsSpan(LegacyMagic.Length, SaltLength).ToArray();
        var nonce = payload.AsSpan(LegacyMagic.Length + SaltLength, NonceLength).ToArray();
        var tag = payload.AsSpan(LegacyMagic.Length + SaltLength + NonceLength, TagLength).ToArray();
        var cipher = payload.AsSpan(LegacyMagic.Length + SaltLength + NonceLength + TagLength).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 150_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        WriteAtomically(destinationPath, stream => stream.Write(plain));
    }

    private static void WriteInt32(Stream output, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static byte[] AssociatedData(long chunkIndex, int length)
    {
        var data = new byte[sizeof(long) + sizeof(int)];
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0, sizeof(long)), chunkIndex);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(sizeof(long)), length);
        return data;
    }

    public static void ReencryptFile(string sourcePath, string destinationPath, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword)) throw new ArgumentException("The existing backup password is required.", nameof(oldPassword));
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("The new backup password is required.", nameof(newPassword));
        EnsureDistinctPaths(sourcePath, destinationPath, "The backup source and destination must differ.");
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
        EnsureNoReparsePointInTree(sourceDirectory, "The backup source directory");
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
        EnsureNoReparsePointInPath(destination, "The restore path");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The restore directory must be empty.");
        var parent = Path.GetDirectoryName(destination);
        if (parent is not null) Directory.CreateDirectory(parent);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        EnsureNoReparsePointInPath(staging, "The restore staging path");
        var temporaryZip = Path.Combine(Path.GetTempPath(), "memento-restore-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            DecryptFile(sourcePath, temporaryZip, password);
            ExtractZipSafely(temporaryZip, staging);
            var result = VerifyManifest(staging);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
            return result;
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static ArchiveRestoreResult ReencryptDirectory(string sourcePath, string destinationPath, string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword)) throw new ArgumentException("The existing backup password is required.", nameof(oldPassword));
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("The new backup password is required.", nameof(newPassword));
        EnsureDistinctPaths(sourcePath, destinationPath, "The backup source and destination must differ.");
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
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var normalizedEntryName = entry.FullName.Replace('\\', '/');
            if (!seenEntries.Add(normalizedEntryName))
                throw new InvalidDataException("Backup contains duplicate archive entries.");
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path.");
            var targetKey = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seenTargets.Add(targetKey))
                throw new InvalidDataException("Backup contains duplicate archive output paths.");
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

    private static void EnsureNoReparsePointInPath(string path, string description)
        => ArchivePathSafety.EnsureNoReparsePointInPath(path, description);

    private static void EnsureNoReparsePointInTree(string root, string description)
    {
        var fullRoot = Path.GetFullPath(root);
        EnsureNoReparsePointInPath(fullRoot, description);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current).ToArray();
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                throw new IOException($"{description} cannot be inspected safely.", error);
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new IOException($"{description} cannot be inspected safely.", error);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"{description} cannot contain a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void EnsureDistinctPaths(string sourcePath, string destinationPath, string message)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(message);
    }

    private static void WriteAtomically(string destinationPath, Action<FileStream> write)
    {
        var destination = Path.GetFullPath(destinationPath);
        EnsureNoReparsePointInPath(destination, "The backup output path");
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

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return new ArchiveRestoreResult(false, ["Backup manifest has no files list."]);

            var root = Path.GetFullPath(restoreDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var pathElement) || !file.TryGetProperty("sha256", out var hashElement) || pathElement.ValueKind != JsonValueKind.String || hashElement.ValueKind != JsonValueKind.String)
                {
                    findings.Add("Backup manifest contains an invalid file entry.");
                    continue;
                }

                var relative = pathElement.GetString() ?? string.Empty;
                var expected = hashElement.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(relative) || !IsSha256(expected))
                {
                    findings.Add($"Backup manifest contains an invalid hash entry: {relative}");
                    continue;
                }
                if (!listed.TryAdd(relative, expected))
                {
                    findings.Add($"Backup manifest contains a duplicate file entry: {relative}");
                    continue;
                }

                var candidate = Path.GetFullPath(Path.Combine(restoreDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                {
                    findings.Add($"Missing backup file: {relative}");
                    continue;
                }

                var actual = HashFile(candidate);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    findings.Add($"Backup hash mismatch: {relative}");
            }

            if (!listed.ContainsKey("archive.sqlite"))
                findings.Add("Backup manifest does not include required archive.sqlite.");

            foreach (var actualPath in Directory.EnumerateFiles(restoreDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(restoreDirectory, actualPath).Replace(Path.DirectorySeparatorChar, '/');
                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!listed.ContainsKey(relative)) findings.Add($"Backup contains an unlisted file: {relative}");
            }
        }
        catch (JsonException error)
        {
            findings.Add($"Backup manifest is not valid JSON: {error.Message}");
        }

        return new ArchiveRestoreResult(findings.Count == 0, findings);
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record ArchiveRestoreResult(bool IntegrityOk, IReadOnlyList<string> Findings);
