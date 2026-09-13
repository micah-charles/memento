using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ArchiveSearchTests
{
    [Fact]
    public void Lexical_search_finds_cantonese_and_english_archive_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "memento-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = new SqliteArchive(Path.Combine(directory, "data", "memory.db"));
            archive.Initialize();
            var repository = new ArchiveRepository(archive);
            var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
            var source = repository.AddSource(new SourceMetadata("source-search", "audio", session.SessionId, null, null, null, null, null, null, null, null, null, null, null, "not_applicable", DateTimeOffset.UtcNow));
            var revision = repository.AddTranscriptRevision(new TranscriptRevision("revision-search", source.SourceId, null, 1, "initial", "我鍾意食魚蛋", 0.9, null, DateTimeOffset.UtcNow));
            var evidence = repository.AddEvidence(new EvidenceRecord("evidence-search", EvidenceKind.DirectStatement, source.SourceId, session.SessionId, null, revision.TranscriptRevisionId, revision.Text, revision.Text, ParticipantCertainty.Stated, false, DateTimeOffset.UtcNow));
            repository.AddMemoryClaim(new MemoryClaim("claim-search", "Participant likes fish balls", null, "likes", "fish balls", ClaimStatus.Candidate, DateTimeOffset.UtcNow));

            var search = new ArchiveSearchService(archive);
            var cantonese = search.Search("魚蛋");
            var english = search.Search("fish balls");

            Assert.Contains(cantonese, hit => hit.RecordType == "transcript_revision" && hit.RecordId == revision.TranscriptRevisionId);
            Assert.Contains(cantonese, hit => hit.RecordType == "evidence" && hit.RecordId == evidence.EvidenceId);
            Assert.Contains(english, hit => hit.RecordType == "memory_claim" && hit.RecordId == "claim-search");
            Assert.Throws<ArgumentOutOfRangeException>(() => search.Search("魚蛋", 0));
            using (var connection = archive.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM memory_search";
                command.ExecuteNonQuery();
            }
            Assert.True(search.Rebuild() >= 3);
            Assert.Contains(search.Search("魚蛋"), hit => hit.RecordId == revision.TranscriptRevisionId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
