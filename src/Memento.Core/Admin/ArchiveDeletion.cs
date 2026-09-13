using System.Text.Json;
using Microsoft.Data.Sqlite;
using Memento.Core.Storage;

namespace Memento.Core.Admin;

public sealed record ArchiveDeletionResult(
    string TombstoneId,
    string SourceId,
    IReadOnlyDictionary<string, int> RemovedCounts,
    bool MediaRemoved,
    IReadOnlyList<string> Findings);

/// <summary>Performs an authenticated, source-scoped deletion while retaining a minimal audit tombstone.</summary>
public sealed class ArchiveDeletionService
{
    private readonly ArchiveRepository _repository;
    private readonly IAdminAuthorizer _authorizer;

    public ArchiveDeletionService(ArchiveRepository repository, IAdminAuthorizer authorizer)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    }

    public ArchiveDeletionResult DeleteSource(string actorId, string sourceId, string reason, DateTimeOffset? occurredAt = null)
    {
        DemandAuthorization(actorId);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("A Source ID is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A deletion reason is required.", nameof(reason));

        var tombstoneId = Guid.NewGuid().ToString("N");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<string>();
        var timestamp = occurredAt ?? DateTimeOffset.UtcNow;

        using (var connection = _repository.Archive.OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            string? sourcePath;
            string? turnId;
            using (var source = connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = "SELECT file_path, turn_id FROM sources WHERE source_id = $source";
                source.Parameters.AddWithValue("$source", sourceId);
                using var reader = source.ExecuteReader();
                if (!reader.Read()) throw new KeyNotFoundException($"Source '{sourceId}' was not found.");
                sourcePath = reader.IsDBNull(0) ? null : reader.GetString(0);
                turnId = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            AddPath(mediaPaths, sourcePath);
            var evidenceIds = ReadIds(connection, transaction, "SELECT evidence_id FROM evidence_records WHERE source_id = $source", ("$source", sourceId));
            var claimIds = ReadIds(connection, transaction, "SELECT DISTINCT memory_claim_id FROM evidence_claim_links WHERE evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id = $source)", ("$source", sourceId));
            var clarificationIds = ReadIds(connection, transaction, "SELECT clarification_event_id FROM clarification_events WHERE source_id = $source", ("$source", sourceId));
            var responseEpisodeIds = ReadIds(connection, transaction, "SELECT response_episode_id FROM response_episodes WHERE stimulus_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id = $source) OR response_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id = $source) OR follow_up_evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id = $source)", ("$source", sourceId));
            var revisionIds = ReadIds(connection, transaction, "SELECT transcript_revision_id FROM transcript_revisions WHERE source_id = $source ORDER BY revision_number DESC", ("$source", sourceId));
            var personIds = ReadIds(connection, transaction, "SELECT DISTINCT person_entity_id FROM evidence_entity_links WHERE evidence_id IN (SELECT evidence_id FROM evidence_records WHERE source_id = $source) UNION SELECT DISTINCT person_entity_id FROM entity_aliases WHERE source_clarification_event_id IN (SELECT clarification_event_id FROM clarification_events WHERE source_id = $source)", ("$source", sourceId));
            var canDeleteTurnAssets = turnId is not null && CountRows(connection, transaction, "SELECT COUNT(*) FROM sources WHERE turn_id = $turn", ("$turn", turnId)) == 1;
            var derivedIds = canDeleteTurnAssets ? ReadIds(connection, transaction, "SELECT derived_speech_asset_id FROM derived_speech_assets WHERE turn_id = $turn", ("$turn", turnId!)) : [];
            if (turnId is not null && !canDeleteTurnAssets)
                findings.Add("Turn-level provider metadata and derived audio were retained because the turn has another Source.");

            counts["review_annotations"] = DeleteByIds(connection, transaction, "review_annotations", "target_id", evidenceIds.Concat(claimIds).Concat(responseEpisodeIds).Concat(clarificationIds).Concat(revisionIds).Concat(personIds).Concat(derivedIds).Concat([sourceId]).Distinct(StringComparer.Ordinal).ToArray());
            counts["response_episodes"] = DeleteByIds(connection, transaction, "response_episodes", "response_episode_id", responseEpisodeIds);
            counts["evidence_claim_links"] = DeleteClaimLinks(connection, transaction, evidenceIds, claimIds);
            counts["evidence_entity_links"] = DeleteByIds(connection, transaction, "evidence_entity_links", "evidence_id", evidenceIds);
            counts["vocabulary_entries"] = DeleteByIds(connection, transaction, "vocabulary_entries", "source_clarification_event_id", clarificationIds);
            counts["entity_aliases"] = DeleteByIds(connection, transaction, "entity_aliases", "source_clarification_event_id", clarificationIds);
            counts["clarification_events"] = DeleteByIds(connection, transaction, "clarification_events", "clarification_event_id", clarificationIds);
            counts["conversation_jobs"] = DeleteByIds(connection, transaction, "conversation_jobs", "source_id", [sourceId]);
            counts["provider_interactions"] = canDeleteTurnAssets ? DeleteByIds(connection, transaction, "provider_interactions", "turn_id", [turnId!]) : 0;

            if (canDeleteTurnAssets)
            {
                var derivedPaths = ReadPaths(connection, transaction, "SELECT file_path FROM derived_speech_assets WHERE turn_id = $turn", ("$turn", turnId!));
                foreach (var path in derivedPaths) AddPath(mediaPaths, path);
                counts["derived_speech_assets"] = DeleteByIds(connection, transaction, "derived_speech_assets", "turn_id", [turnId!]);
            }
            else
            {
                counts["derived_speech_assets"] = 0;
            }

            counts["evidence_records"] = DeleteByIds(connection, transaction, "evidence_records", "evidence_id", evidenceIds);
            counts["memory_claims"] = DeleteByIds(connection, transaction, "memory_claims", "memory_claim_id", claimIds);
            counts["person_entities"] = DeleteOrphanPeople(connection, transaction, personIds);
            counts["memory_search"] = ArchiveSearchIndex.Remove(connection, transaction, "transcript_revision", revisionIds)
                + ArchiveSearchIndex.Remove(connection, transaction, "evidence", evidenceIds)
                + ArchiveSearchIndex.Remove(connection, transaction, "memory_claim", claimIds);
            counts["transcript_revisions"] = DeleteRevisions(connection, transaction, revisionIds);
            counts["sources"] = DeleteByIds(connection, transaction, "sources", "source_id", [sourceId]);

            using var tombstone = connection.CreateCommand();
            tombstone.Transaction = transaction;
            tombstone.CommandText = "INSERT INTO deletion_tombstones(deletion_tombstone_id, target_type, target_id, actor_id, reason, removed_counts_json, media_removed, occurred_at) VALUES ($id, 'source', $target, $actor, $reason, $counts, 0, $occurred)";
            tombstone.Parameters.AddWithValue("$id", tombstoneId);
            tombstone.Parameters.AddWithValue("$target", sourceId);
            tombstone.Parameters.AddWithValue("$actor", actorId);
            tombstone.Parameters.AddWithValue("$reason", reason.Trim());
            tombstone.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(counts));
            tombstone.Parameters.AddWithValue("$occurred", timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            tombstone.ExecuteNonQuery();
            transaction.Commit();
        }

        var mediaRemoved = true;
        foreach (var path in mediaPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
            }
            catch (Exception error)
            {
                mediaRemoved = false;
                findings.Add($"Could not remove media asset '{Path.GetFileName(path)}': {error.GetType().Name}.");
            }
        }

        try
        {
            using var connection = _repository.Archive.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE deletion_tombstones SET media_removed = $removed WHERE deletion_tombstone_id = $id";
            command.Parameters.AddWithValue("$removed", mediaRemoved ? 1 : 0);
            command.Parameters.AddWithValue("$id", tombstoneId);
            command.ExecuteNonQuery();
        }
        catch (Exception error)
        {
            findings.Add($"Could not update deletion tombstone media status: {error.GetType().Name}.");
        }

        return new ArchiveDeletionResult(tombstoneId, sourceId, counts, mediaRemoved, findings);
    }

    private void DemandAuthorization(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || !_authorizer.IsAuthorized(actorId))
            throw new UnauthorizedAccessException("Family Admin authorization is required for deletion.");
    }

    private static List<string> ReadIds(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static List<string> ReadPaths(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
        => ReadIds(connection, transaction, sql, parameters);

    private static int CountRows(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int DeleteRevisions(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> revisionIds)
    {
        var deleted = 0;
        foreach (var revisionId in revisionIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM transcript_revisions WHERE transcript_revision_id = $id";
            command.Parameters.AddWithValue("$id", revisionId);
            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }

    private static int DeleteByIds(SqliteConnection connection, SqliteTransaction transaction, string table, string column, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            parameters[index] = "$id" + index;
            command.Parameters.AddWithValue(parameters[index], ids[index]);
        }

        command.CommandText = $"DELETE FROM {table} WHERE {column} IN ({string.Join(",", parameters)})";
        return command.ExecuteNonQuery();
    }

    private static int DeleteClaimLinks(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> evidenceIds, IReadOnlyList<string> claimIds)
    {
        if (evidenceIds.Count == 0 && claimIds.Count == 0) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var evidenceParameters = evidenceIds.Select((_, index) => "$evidence" + index).ToArray();
        var claimParameters = claimIds.Select((_, index) => "$claim" + index).ToArray();
        for (var index = 0; index < evidenceIds.Count; index++) command.Parameters.AddWithValue(evidenceParameters[index], evidenceIds[index]);
        for (var index = 0; index < claimIds.Count; index++) command.Parameters.AddWithValue(claimParameters[index], claimIds[index]);
        var clauses = new List<string>();
        if (evidenceParameters.Length > 0) clauses.Add("evidence_id IN (" + string.Join(",", evidenceParameters) + ")");
        if (claimParameters.Length > 0) clauses.Add("memory_claim_id IN (" + string.Join(",", claimParameters) + ")");
        command.CommandText = "DELETE FROM evidence_claim_links WHERE " + string.Join(" OR ", clauses);
        return command.ExecuteNonQuery();
    }

    private static int DeleteOrphanPeople(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> personIds)
    {
        var deleted = 0;
        foreach (var personId in personIds.Distinct(StringComparer.Ordinal))
        {
            using var references = connection.CreateCommand();
            references.Transaction = transaction;
            references.CommandText = "SELECT (SELECT COUNT(*) FROM evidence_entity_links WHERE person_entity_id = $person) + (SELECT COUNT(*) FROM entity_aliases WHERE person_entity_id = $person)";
            references.Parameters.AddWithValue("$person", personId);
            if (Convert.ToInt32(references.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0) continue;

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM person_entities WHERE person_entity_id = $person";
            delete.Parameters.AddWithValue("$person", personId);
            deleted += delete.ExecuteNonQuery();
        }

        return deleted;
    }

    private static void AddPath(ISet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
    }
}
