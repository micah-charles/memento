using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ArchiveSearchTests
{
    [Fact]
    public void Transcript_search_hit_retains_source_session_provenance()
    {
        using var fixture = new SearchFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(new SourceMetadata(
            "source-search-session", "audio", session.SessionId, null, fixture.AudioPath,
            "PCM WAV", 16000, 1, 16, 4, 0, "abc", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        repository.AddTranscriptRevision(new TranscriptRevision(
            "revision-search-session", source.SourceId, null, 1, "initial",
            "今日飲茶", null, null, DateTimeOffset.UtcNow));

        var hit = Assert.Single(new ArchiveSearchService(archive).Search("飲茶"));

        Assert.Equal("transcript_revision", hit.RecordType);
        Assert.Equal(source.SourceId, hit.SourceId);
        Assert.Equal(session.SessionId, hit.SessionId);
    }

    [Fact]
    public void Rebuilding_transcript_index_reconstructs_session_provenance()
    {
        using var fixture = new SearchFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(new SourceMetadata(
            "source-rebuild-session", "audio", session.SessionId, null, fixture.AudioPath,
            "PCM WAV", 16000, 1, 16, 4, 0, "abc", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        repository.AddTranscriptRevision(new TranscriptRevision(
            "revision-rebuild-session", source.SourceId, null, 1, "initial",
            "重建索引測試", null, null, DateTimeOffset.UtcNow));
        var search = new ArchiveSearchService(archive);

        search.Rebuild();
        var hit = Assert.Single(search.Search("重建"));

        Assert.Equal(session.SessionId, hit.SessionId);
    }

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
            Assert.Equal(0, ArchiveHealthCheck.Run(archive, directory).InvalidSearchIndexCount);
            using (var connection = archive.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM memory_search";
                command.ExecuteNonQuery();
            }
            Assert.Equal(3, ArchiveHealthCheck.Run(archive, directory).InvalidSearchIndexCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class SearchFixture : IDisposable
    {
        public SearchFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-search-provenance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "data", "memory.db");
            AudioPath = Path.Combine(DirectoryPath, "raw", "audio", "test.wav");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string AudioPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
