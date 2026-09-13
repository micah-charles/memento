using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class ClarificationTests
{
    [Fact]
    public void Name_correction_preserves_every_revision_and_speaker_authority()
    {
        using var fixture = new ClarificationFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        repository.AddConsent(session.SessionId, ConsentScope.CloudTranscription, PrivacyMode.Normal, true, "privacy-1");
        var source = repository.AddSource(new SourceMetadata("source-name", "audio", session.SessionId, null, "raw/name.wav", "PCM WAV", 48000, 1, 16, 100, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow));
        var protocol = new ClarificationProtocol(repository);
        var initial = protocol.AddInitialRevision(source.SourceId, null, "我細個有個friend叫阿珍", 0.97);

        var chain = protocol.RecordOutcome(session, initial, ClarificationEntityKind.PersonName, "你頭先個朋友個名，我聽到好似『阿珍』，係咪？", "唔係呀，貞潔個貞，阿貞。", ClarificationOutcome.SpeakerConfirmed, "我細個有個friend叫阿貞", "阿貞", "childhood friend");

        Assert.Equal("我細個有個friend叫阿珍", chain.InitialRevision.Text);
        Assert.Equal("我細個有個friend叫阿貞", chain.CorrectedRevision!.Text);
        Assert.Equal(initial.TranscriptRevisionId, chain.CorrectedRevision.ParentRevisionId);
        Assert.True(chain.Vocabulary!.SpeakerConfirmed);
        Assert.Equal("阿貞", chain.Vocabulary.CanonicalText);
        Assert.Equal(ClarificationOutcome.SpeakerConfirmed, chain.Event.Outcome);
        Assert.Equal(source.SourceId, chain.Event.SourceId);
        Assert.True(repository.HasActiveConversationJob(session.SessionId, source.SourceId, "durable_extraction", chain.CorrectedRevision!.TranscriptRevisionId));
    }

    [Fact]
    public void Uncertain_school_year_keeps_both_possibilities_without_deciding()
    {
        using var fixture = new ClarificationFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-year"));
        var protocol = new ClarificationProtocol(repository);
        var initial = protocol.AddInitialRevision(source.SourceId, null, "好似小三定小四", 0.61);

        var chain = protocol.RecordOutcome(session, initial, ClarificationEntityKind.Year, "你記得係小三定小四？", "唔肯定，應該係小三或者小四。", ClarificationOutcome.TwoPossibilities);

        Assert.Null(chain.CorrectedRevision);
        Assert.Null(chain.Vocabulary);
        Assert.Equal(ClarificationOutcome.TwoPossibilities, chain.Event.Outcome);
        Assert.Contains("小三或者小四", chain.Event.ParticipantResponseText);
    }

    [Fact]
    public void Refusal_and_dont_remember_remain_explicit_outcomes()
    {
        using var fixture = new ClarificationFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-refusal"));
        var unknownSource = repository.AddSource(fixture.Source(session.SessionId, "source-unknown"));
        var protocol = new ClarificationProtocol(repository);
        var refused = protocol.RecordOutcome(session, protocol.AddInitialRevision(source.SourceId, null, "阿珍", 0.5), ClarificationEntityKind.PersonName, "係咪阿珍？", "唔想講住。", ClarificationOutcome.ParticipantRefused);
        var unknown = protocol.RecordOutcome(session, protocol.AddInitialRevision(unknownSource.SourceId, null, "二零二零年", 0.5), ClarificationEntityKind.Date, "係咪二零二零年？", "唔記得。", ClarificationOutcome.ParticipantDoesNotRemember);

        Assert.Equal(ClarificationOutcome.ParticipantRefused, refused.Event.Outcome);
        Assert.Equal(ClarificationOutcome.ParticipantDoesNotRemember, unknown.Event.Outcome);
        Assert.Null(refused.CorrectedRevision);
        Assert.Null(unknown.CorrectedRevision);
    }

    [Fact]
    public void Correction_of_a_correction_is_a_new_revision_with_parent()
    {
        using var fixture = new ClarificationFixture();
        using var archive = fixture.CreateArchive();
        var repository = new ArchiveRepository(archive);
        var session = repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal);
        var source = repository.AddSource(fixture.Source(session.SessionId, "source-revision"));
        var protocol = new ClarificationProtocol(repository);
        var initial = protocol.AddInitialRevision(source.SourceId, null, "阿珍", 0.4);
        var first = protocol.RecordOutcome(session, initial, ClarificationEntityKind.PersonName, "係咪阿珍？", "係阿貞。", ClarificationOutcome.SpeakerConfirmed, "阿貞");
        var second = protocol.RecordOutcome(session, first.CorrectedRevision!, ClarificationEntityKind.PersonName, "我再確認，係阿貞？", "唔係，係阿正。", ClarificationOutcome.CorrectedPreviousCorrection, "阿正");

        Assert.Equal(2, first.CorrectedRevision!.RevisionNumber);
        Assert.Equal(3, second.CorrectedRevision!.RevisionNumber);
        Assert.Equal(first.CorrectedRevision.TranscriptRevisionId, second.CorrectedRevision.ParentRevisionId);
        Assert.Equal("阿正", second.Vocabulary!.CanonicalText);
    }

    [Fact]
    public void Policy_asks_for_high_value_ambiguity_but_avoids_low_impact_known_word()
    {
        Assert.True(ClarificationPolicy.Decide(new ClarificationCandidate("阿珍", ClarificationEntityKind.PersonName, 0.99, false, true)).ShouldAsk);
        Assert.True(ClarificationPolicy.Decide(new ClarificationCandidate("2020", ClarificationEntityKind.Year, 0.98, true, false)).ShouldAsk);
        Assert.False(ClarificationPolicy.Decide(new ClarificationCandidate("嗯", ClarificationEntityKind.None, 0.7, false, false)).ShouldAsk);
        Assert.True(ClarificationPolicy.Decide(new ClarificationCandidate("???", ClarificationEntityKind.None, 0.2, false, false)).ShouldAsk);
    }

    private sealed class ClarificationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "memento-clarification-tests", Guid.NewGuid().ToString("N"));
        public ClarificationFixture() => Directory.CreateDirectory(_directory);
        public SqliteArchive CreateArchive() { var archive = new SqliteArchive(Path.Combine(_directory, "data", "memory.db")); archive.Initialize(); return archive; }
        public SourceMetadata Source(string sessionId, string id) => new(id, "audio", sessionId, null, "raw/audio.wav", "PCM WAV", 48000, 1, 16, 100, 1, "abc", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "finalized", DateTimeOffset.UtcNow);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
