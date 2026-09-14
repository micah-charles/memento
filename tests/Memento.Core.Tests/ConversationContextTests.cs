using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ConversationContextTests
{
    [Fact]
    public void Context_is_bounded_to_latest_revision_per_source_and_same_session()
    {
        using var fixture = new ContextFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow.AddMinutes(-10), PrivacyMode.Normal);
        var otherSession = repository.AddSession(DateTimeOffset.UtcNow.AddMinutes(-20), PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.LocalCapture, PrivacyMode.Normal, true, "privacy-1");

        var olderSource = AddSource(repository, session.SessionId, "older-source", fixture.FilePath("older.wav"));
        var newerSource = AddSource(repository, session.SessionId, "newer-source", fixture.FilePath("newer.wav"));
        var currentSource = AddSource(repository, session.SessionId, "current-source", fixture.FilePath("current.wav"));
        var otherSource = AddSource(repository, otherSession.SessionId, "other-source", fixture.FilePath("other.wav"));

        var olderTime = DateTimeOffset.UtcNow.AddMinutes(-8);
        repository.AddTranscriptRevision(new TranscriptRevision("older-initial", olderSource.SourceId, null, 1, "initial", "舊版本", null, null, olderTime));
        repository.AddTranscriptRevision(new TranscriptRevision("older-corrected", olderSource.SourceId, null, 2, "corrected", "阿貞係小學朋友", null, "older-initial", olderTime.AddSeconds(1)));
        repository.AddTranscriptRevision(new TranscriptRevision("newer-initial", newerSource.SourceId, null, 1, "initial", "我鍾意飲茶", null, null, olderTime.AddMinutes(1)));
        repository.AddTranscriptRevision(new TranscriptRevision("current-initial", currentSource.SourceId, null, 1, "initial", "今次內容", null, null, olderTime.AddMinutes(2)));
        repository.AddTranscriptRevision(new TranscriptRevision("other-initial", otherSource.SourceId, null, 1, "initial", "唔應該出現", null, null, olderTime.AddMinutes(3)));

        var context = new ConversationContextBuilder(repository).Build(session.SessionId, currentSource.SourceId, maxItems: 4);

        Assert.Equal("阿貞係小學朋友\n我鍾意飲茶", context);
    }

    [Fact]
    public void Context_returns_null_when_session_has_no_previous_transcript()
    {
        using var fixture = new ContextFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);

        Assert.Null(new ConversationContextBuilder(repository).Build(session.SessionId));
    }

    [Fact]
    public void Context_rejects_invalid_bounds()
    {
        using var fixture = new ContextFixture();
        using var archive = new SqliteArchive(fixture.DatabasePath);
        archive.Initialize();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var builder = new ConversationContextBuilder(repository);

        Assert.Throws<ArgumentException>(() => builder.Build(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(session.SessionId, maxItems: 0));
    }

    private static SourceMetadata AddSource(ArchiveRepository repository, string sessionId, string id, string path)
        => repository.AddSource(new SourceMetadata(id, "audio", sessionId, null, path, "PCM WAV", 48000, 1, 16, 4, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));

    private sealed class ContextFixture : IDisposable
    {
        public ContextFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "memento-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string DatabasePath => Path.Combine(DirectoryPath, "memory.db");
        public string FilePath(string file) => System.IO.Path.Combine(DirectoryPath, file);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
        }
    }
}
