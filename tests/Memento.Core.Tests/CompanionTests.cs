using System.Text.Json;
using Memento.Core.Companion;
using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Tests;

public sealed class CompanionTests
{
    [Fact]
    public async Task Three_turns_share_thread_and_archive_exact_input_and_original_answer()
    {
        using var f = new Fixture(); var backend = new FakeBackend();
        await using var coordinator = f.Coordinator(backend);
        await coordinator.StartAsync("fake", PrivacyMode.Normal, false, true);
        await coordinator.SendTextAsync("紫色茶杯"); await coordinator.SendTextAsync("今日飲茶"); await coordinator.SendTextAsync("暗號？");
        Assert.Single(backend.Threads.Distinct());
        Assert.Equal(4, backend.Inputs.Count);
        var timeline = f.Store.Timeline(coordinator.SessionId!);
        Assert.Equal(8, timeline.Count);
        Assert.Contains(timeline, i => i.Role == "participant" && i.Text == "紫色茶杯");
        var original = timeline.First(i => i.Role == "participant");
        f.Store.AddRevision(original.MessageId, "修正版本", "family-correction", "reviewer", "correction", original.RevisionId);
        Assert.Contains(f.Store.Timeline(coordinator.SessionId!), i => i.RevisionId == original.RevisionId && i.Text == "紫色茶杯");
        Assert.Equal(CompanionState.Listening, coordinator.State);
        await coordinator.EndAsync(); Assert.Equal(CompanionState.Ended, coordinator.State);
    }

    [Theory]
    [InlineData(PrivacyMode.LocalCaptureOnly, true)]
    [InlineData(PrivacyMode.PrivateConversation, true)]
    [InlineData(PrivacyMode.Normal, false)]
    public async Task Privacy_blocks_backend_before_any_call(PrivacyMode mode, bool consent)
    {
        using var f = new Fixture(); var backend = new FakeBackend(); await using var coordinator = f.Coordinator(backend);
        await Assert.ThrowsAsync<CloudConsentRequiredException>(() => coordinator.StartAsync("fake", mode, false, consent));
        Assert.Empty(backend.Inputs); Assert.Equal(0, backend.Begins);
    }

    [Fact]
    public void Segmented_pcm_and_cross_file_spans_preserve_every_sample()
    {
        using var f = new Fixture(); var session = f.Session();
        using var capture = new ContinuousCapture(f.Store, f.Audio, session.SessionId, segmentSeconds: 1);
        var bytes = Enumerable.Range(0, 48000 * 2 * 3 + 100).Select(i => (byte)(i % 251)).ToArray();
        capture.Append(bytes.AsSpan(0, 200)); capture.Append(bytes.AsSpan(200)); capture.FlushSegment();
        var message = f.Store.AddMessage(session.SessionId, "participant", "test", "initial");
        var spans = capture.Spans(0, bytes.Length / 2);
        Assert.Equal(4, spans.Count);
        using var reconstructed = new MemoryStream();
        foreach (var span in spans) { f.Store.AddSpan(message.MessageId, span); reconstructed.Write(new SourceSpanReader(f.Repository, f.Audio).Read(span)); }
        Assert.Equal(bytes, reconstructed.ToArray());
        var other = f.Session(); var wrong = f.Store.AddMessage(other.SessionId, "participant", "other", "initial");
        Assert.Throws<InvalidDataException>(() => f.Store.AddSpan(wrong.MessageId, spans[0]));
        Assert.Throws<InvalidDataException>(() => f.Store.AddSpan(message.MessageId, spans[0] with { EndSample = long.MaxValue }));
    }

    [Fact]
    public void Endpointing_waits_for_speech_and_preserves_elderly_pause()
    {
        var detector = new EnergyUtteranceDetector(); var quiet = new byte[9600];
        for (var i = 0; i < 100; i++) Assert.False(detector.Append(quiet));
        var voice = new byte[9600]; for (var i = 0; i < voice.Length; i += 2) voice[i + 1] = 20;
        Assert.False(detector.Append(voice));
        for (var i = 0; i < 17; i++) Assert.False(detector.Append(quiet));
        Assert.True(detector.Append(quiet)); detector.Reset(); Assert.False(detector.HasSpeech);
    }

    [Fact]
    public void Whisper_parser_preserves_text_and_timestamps()
    {
        var result = WhisperLocalTranscription.Parse("""{"transcription":[{"offsets":{"from":0,"to":900},"text":"你好"},{"offsets":{"from":900,"to":1300},"text":"呀"}]}""");
        Assert.Equal("你好呀", result.Text); Assert.Equal(1300, result.Segments[1].EndMs);
    }

    [Fact]
    public async Task Cancelled_late_backend_response_is_not_archived_or_played_as_completed()
    {
        using var f = new Fixture(); var backend = new FakeBackend(); await using var coordinator = f.Coordinator(backend);
        await coordinator.StartAsync("fake", PrivacyMode.Normal, false, true);
        backend.Delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = coordinator.SendTextAsync("waiting");
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = coordinator.EndAsync(); backend.Delay.SetResult();
        await Task.WhenAll(send, stop);
        Assert.Equal(CompanionState.Ended, coordinator.State);
        Assert.DoesNotContain(f.Store.Timeline(coordinator.SessionId!), x => x.Role == "assistant" && x.Text == "reply: waiting");
    }

    [Fact]
    public void Source_withdrawal_blocks_entire_context_and_filtered_export()
    {
        using var f = new Fixture(); var session = f.Session();
        using var capture = new ContinuousCapture(f.Store, f.Audio, session.SessionId, 1);
        capture.Append(new byte[9600]); capture.FlushSegment();
        var message = f.Store.AddMessage(session.SessionId, "participant", "private test", "initial");
        var span = Assert.Single(capture.Spans(0, 4800)); f.Store.AddSpan(message.MessageId, span);
        using (var c = f.Archive.OpenConnection()) { using var q = c.CreateCommand(); q.CommandText = "UPDATE sources SET recovery_status='withdrawn' WHERE source_id=$id"; q.Parameters.AddWithValue("$id", span.SourceId); q.ExecuteNonQuery(); }
        Assert.Throws<InvalidOperationException>(() => f.Store.EnsureCloudAllowed(session.SessionId));
        var exported = ArchiveExporter.Export(f.Archive, Path.Combine(f.Root, "exports"));
        Assert.DoesNotContain("private test", File.ReadAllText(Path.Combine(exported.ExportDirectory, "companion_text_versions.jsonl")));
        Assert.Contains(f.Store.Timeline(session.SessionId), item => item.Text == "private test");
    }

    private sealed class FakeBackend : ICompanionBackend
    {
        public List<string> Inputs { get; } = []; public List<string> Threads { get; } = []; public int Begins;
        public TaskCompletionSource? Delay; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CompanionCapabilities> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CompanionCapabilities(true, [], "test"));
        public Task<CompanionThread> BeginAsync(string model, string? resumeId = null, CancellationToken cancellationToken = default) { Begins++; return Task.FromResult(new CompanionThread(Guid.NewGuid().ToString(), model)); }
        public async Task<CompanionReply> SendAsync(CompanionThread thread, string text, Action<string>? onText = null, CancellationToken cancellationToken = default)
        { Inputs.Add(text); Threads.Add(thread.Id); if (Delay is not null) { Entered.TrySetResult(); await Delay.Task; } return new(thread.Id, "turn", "request", thread.Model, "reply: " + text); }
        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "memento-companion-" + Guid.NewGuid().ToString("N"));
        public string Audio => Path.Combine(Root, "raw", "audio");
        public SqliteArchive Archive { get; }
        public ArchiveRepository Repository { get; }
        public CompanionArchive Store { get; }
        public Fixture() { Archive = new(Path.Combine(Root, "data", "memory.db")); Archive.Initialize(); Repository = new(Archive); Store = new(Repository); }
        public Session Session() { var s = Repository.AddSession(DateTimeOffset.UtcNow, PrivacyMode.Normal); Repository.AddConsent(s.SessionId, ConsentScope.CloudConversation, s.PrivacyMode, true, "test"); Store.RegisterSession(s, "test"); return s; }
        public ConversationCoordinator Coordinator(ICompanionBackend backend) => new(Store, backend, Audio, new DerivedAudioStore(Repository, Path.Combine(Root, "derived")));
        public void Dispose() { Archive.Dispose(); Directory.Delete(Root, true); }
    }
}
