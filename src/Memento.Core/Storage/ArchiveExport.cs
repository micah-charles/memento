using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Memento.Core.Storage;

public sealed record ArchiveExportResult(string ExportDirectory, string ManifestPath, IReadOnlyDictionary<string, string> FileHashes);

public static class ArchiveExporter
{
    private static readonly string[] Tables = ["sessions", "turns", "consent_events", "sources", "provider_interactions", "transcript_revisions", "clarification_events", "vocabulary_entries", "conversation_jobs", "evidence_records", "memory_claims", "evidence_claim_links", "person_entities", "entity_aliases", "evidence_entity_links", "review_annotations"];

    public static ArchiveExportResult Export(SqliteArchive archive, string destinationDirectory, bool includeMedia = false)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("An export directory is required.", nameof(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        var exportDirectory = Path.Combine(destinationDirectory, "memento-export-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(exportDirectory);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = archive.OpenConnection();
        foreach (var table in Tables)
        {
            var relative = table + ".jsonl";
            var path = Path.Combine(exportDirectory, relative);
            WriteTable(connection, table, path);
            hashes[relative] = Hash(path);
        }

        var backupPath = Path.Combine(exportDirectory, "archive.sqlite");
        using (var backup = connection.CreateCommand())
        {
            backup.CommandText = "VACUUM INTO $path";
            backup.Parameters.AddWithValue("$path", backupPath);
            backup.ExecuteNonQuery();
        }
        hashes["archive.sqlite"] = Hash(backupPath);

        if (includeMedia)
        {
            var mediaDirectory = Path.Combine(exportDirectory, "media");
            Directory.CreateDirectory(mediaDirectory);
            using var sources = connection.CreateCommand();
            sources.CommandText = "SELECT file_path FROM sources WHERE file_path IS NOT NULL";
            using var reader = sources.ExecuteReader();
            while (reader.Read())
            {
                var sourcePath = reader.GetString(0);
                if (!File.Exists(sourcePath)) continue;
                var filename = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(filename)) continue;
                var target = Path.Combine(mediaDirectory, filename);
                File.Copy(sourcePath, target, overwrite: false);
                hashes[Path.Combine("media", filename).Replace('\\', '/')] = Hash(target);
            }
        }

        var manifestPath = Path.Combine(exportDirectory, "manifest.json");
        var manifest = new { schema_version = archive.CurrentSchemaVersion, exported_at = DateTimeOffset.UtcNow, files = hashes.OrderBy(item => item.Key).Select(item => new { path = item.Key, sha256 = item.Value }).ToArray() };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        hashes["manifest.json"] = Hash(manifestPath);
        return new ArchiveExportResult(exportDirectory, manifestPath, hashes);
    }

    private static void WriteTable(SqliteConnection connection, string table, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}";
        using var reader = command.ExecuteReader();
        using var writer = new StreamWriter(path, false);
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            writer.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
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
        using var stream = File.Create(destinationPath);
        stream.Write(Magic); stream.Write(salt); stream.Write(nonce); stream.Write(tag); stream.Write(cipher);
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
        File.WriteAllBytes(destinationPath, plain);
    }
}
