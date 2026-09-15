using Memento.Core.Audio;
using Memento.Core.Conversation;
using Memento.Core.Domain;

namespace Memento.Core.Companion;

public enum CompanionState { Idle, Greeting, Listening, Transcribing, Thinking, Speaking, RecoverableError, Ending, Ended }
public sealed record CompanionStatus(CompanionState State, string Message);

/// <summary>Owns one session and one response pipeline. Audio archival continues while AI is speaking.</summary>
public sealed class ConversationCoordinator : IAsyncDisposable
{
    private readonly CompanionArchive _archive;
    private readonly ICompanionBackend _backend;
    private readonly ILocalTranscriptionProvider? _transcription;
    private readonly ISpeechOutputProvider? _speech;
    private readonly ISpeechOutputPlayback? _playback;
    private readonly DerivedAudioStore _speechStore;
    private readonly string _audioRoot;
    private readonly IUtteranceDetector _detector;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _sessionStop = new();
    private CancellationTokenSource? _responseStop;
    private Task _response = Task.CompletedTask;
    private Task _policyWatch = Task.CompletedTask;
    private Task? _endTask;
    private ContinuousCapture? _capture;
    private Session? _session;
    private CompanionThread? _thread;
    private MemoryStream _buffer = new();
    private long _bufferStart;
    private volatile bool _ending;
    public CompanionState State { get; private set; } = CompanionState.Idle;
    public string? SessionId => _session?.SessionId;
    public event Action<CompanionStatus>? StatusChanged;

    public ConversationCoordinator(CompanionArchive archive, ICompanionBackend backend, string audioRoot, DerivedAudioStore speechStore,
        ILocalTranscriptionProvider? transcription = null, ISpeechOutputProvider? speech = null, ISpeechOutputPlayback? playback = null, IUtteranceDetector? detector = null)
    { _archive = archive; _backend = backend; _audioRoot = audioRoot; _speechStore = speechStore; _transcription = transcription; _speech = speech; _playback = playback; _detector = detector ?? new EnergyUtteranceDetector(); }

    public async Task StartAsync(string model, PrivacyMode privacyMode, bool localConsent, bool cloudConsent, Func<IAudioInput>? inputFactory = null)
    {
        if (State != CompanionState.Idle) throw new InvalidOperationException("Conversation already started.");
        // Guard before even probing/starting the backend process's remote session.
        if (privacyMode != PrivacyMode.Normal || !cloudConsent) throw new CloudConsentRequiredException();
        if (inputFactory is not null && !localConsent) throw new ConsentRequiredException();
        _session = _archive.Repository.AddSession(DateTimeOffset.UtcNow, privacyMode);
        _archive.Repository.AddConsent(_session.SessionId, ConsentScope.LocalCapture, privacyMode, localConsent, "companion-1");
        _archive.Repository.AddConsent(_session.SessionId, ConsentScope.CloudConversation, privacyMode, cloudConsent, "companion-1");
        _archive.RegisterSession(_session, model);
        SetState(CompanionState.Greeting, "準備同你傾偈…");
        try
        {
            _thread = await _backend.BeginAsync(model, cancellationToken: _sessionStop.Token);
            _archive.SetThread(_session.SessionId, _thread);
            if (inputFactory is not null)
            {
                _capture = new(_archive, _audioRoot, _session.SessionId);
                _capture.Chunk += OnAudio;
                _capture.Failed += OnCaptureFailure;
                _capture.Start(inputFactory());
            }
            _policyWatch = WatchPolicyAsync();
            await QueueResponseAsync("請用一句自然廣東話打招呼，再問我今日點。", "system", null, null, "greeting-request");
        }
        catch { await EndAsync(); throw; }
    }

    public Task SendTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        return QueueResponseAsync(text, "participant", null, null, "typed");
    }

    private void OnAudio(CapturedChunk chunk)
    {
        byte[]? utterance = null; long start = 0; long end = 0;
        lock (_gate)
        {
            if (State != CompanionState.Listening || _ending || !_response.IsCompleted) return;
            if (_buffer.Length == 0) _bufferStart = chunk.StartSample;
            _buffer.Write(chunk.Pcm);
            var finished = _detector.Append(chunk.Pcm);
            if (!_detector.HasSpeech && _buffer.Length >= 48000) { _buffer.SetLength(0); _buffer.Position = 0; }
            if (finished || (_detector.HasSpeech && _buffer.Length >= 48000 * 2 * 45))
            {
                utterance = _buffer.ToArray(); start = _bufferStart; end = chunk.StartSample + chunk.Pcm.Length / 2;
                _buffer.SetLength(0); _buffer.Position = 0; _detector.Reset();
                State = CompanionState.Transcribing; // reserve pipeline before another audio callback
            }
        }
        if (utterance is not null)
        {
            var pcm = utterance;
            _ = QueueResponseAsync(null, "participant", pcm, (start, end), "stt-initial");
        }
    }
    private void OnCaptureFailure(Exception error)
    {
        _sessionStop.Cancel();
        SetState(CompanionState.RecoverableError, "咪高峰或儲存發生問題；請結束後再試，已有錄音會保留。");
        _ = Task.Run(EndAsync);
    }

    private Task QueueResponseAsync(string? text, string role, byte[]? pcm, (long Start, long End)? range, string kind)
    {
        lock (_gate)
        {
            if (_ending || _session is null || _thread is null) throw new InvalidOperationException("Conversation is not active.");
            if (!_response.IsCompleted) throw new InvalidOperationException("請等目前回覆完成。");
            _responseStop?.Dispose();
            _responseStop = CancellationTokenSource.CreateLinkedTokenSource(_sessionStop.Token);
            var token = _responseStop.Token;
            _response = Task.Run(() => RespondAsync(text, role, pcm, range, kind, token));
            return _response;
        }
    }

    private async Task RespondAsync(string? text, string role, byte[]? pcm, (long Start, long End)? range, string kind, CancellationToken token)
    {
        string? inputMessage = null; string? answerMessage = null; string? inputRevision = null;
        string? answerRevision = null; string? playbackId = null; string partial = "";
        IReadOnlyList<SpeechSegment>? transcriptSegments = null;
        try
        {
            EnsureAllowed(token);
            if (pcm is not null)
            {
                SetState(CompanionState.Transcribing, "聽緊你講嘅意思…");
                _capture!.FlushSegment();
                var transcript = await (_transcription ?? throw new InvalidOperationException("本機語音辨識未設定。")).TranscribeAsync(pcm, token);
                transcriptSegments = transcript.Segments;
                text = transcript.Text;
                if (string.IsNullOrWhiteSpace(text)) { SetState(CompanionState.Listening, "未聽清楚，可以再講一次。"); return; }
            }
            EnsureAllowed(token);
            var input = _archive.AddMessage(_session!.SessionId, role, text!, kind, "sending");
            inputMessage = input.MessageId; inputRevision = input.RevisionId;
            if (transcriptSegments is not null) _archive.AddTranscriptSegments(inputMessage, transcriptSegments);
            if (range is not null)
                foreach (var span in _capture!.Spans(range.Value.Start, range.Value.End)) _archive.AddSpan(inputMessage, span);
            SetState(CompanionState.Thinking, "諗緊點樣回覆…");
            var reply = await _backend.SendAsync(_thread!, text!, s => partial = s, token);
            EnsureAllowed(token);
            _archive.MarkMessage(inputMessage, "sent", reply, inputRevision);
            var answer = _archive.AddMessage(_session.SessionId, "assistant", reply.Text, "ai-original", "received");
            answerMessage = answer.MessageId; answerRevision = answer.RevisionId;
            _archive.MarkMessage(answerMessage, "received", reply, inputRevision);
            if (_speech is not null && _playback is not null)
            {
                var output = await _speech.SynthesizeAsync(reply.Text, token);
                EnsureAllowed(token);
                var asset = _speechStore.Store(_session.SessionId, answer.Turn.TurnId, output);
                playbackId = _archive.StartPlayback(answerMessage, answerRevision, asset.DerivedSpeechAssetId, _capture?.Position ?? 0);
                SetState(CompanionState.Speaking, reply.Text);
                await _playback.PlayAsync(asset, token);
                EnsureAllowed(token);
                _archive.EndPlayback(playbackId, _capture?.Position ?? 0, "completed"); playbackId = null;
                _archive.MarkMessage(answerMessage, "spoken", reply, inputRevision);
            }
            SetState(CompanionState.Listening, reply.Text);
        }
        catch (OperationCanceledException)
        {
            if (inputMessage is not null) _archive.MarkMessage(inputMessage, "cancelled", inputRevision: inputRevision);
            if (answerMessage is not null) _archive.MarkMessage(answerMessage, "interrupted", inputRevision: inputRevision);
            else if (!string.IsNullOrEmpty(partial) && _session is not null)
            {
                try { _archive.EnsureCloudAllowed(_session.SessionId); _archive.AddMessage(_session.SessionId, "assistant", partial, "ai-partial", "cancelled"); }
                catch (InvalidOperationException) { }
            }
            if (!_ending) SetState(CompanionState.Listening, "好，你講。");
        }
        catch (Exception error)
        {
            if (inputMessage is not null) _archive.MarkMessage(inputMessage, "failed-or-uncertain", inputRevision: inputRevision);
            if (answerMessage is not null) _archive.MarkMessage(answerMessage, "playback-failed", inputRevision: inputRevision);
            else if (!string.IsNullOrWhiteSpace(partial) && _session is not null)
            {
                try { _archive.EnsureCloudAllowed(_session.SessionId); _archive.AddMessage(_session.SessionId, "assistant", partial, "ai-partial", "failed"); }
                catch (InvalidOperationException) { }
            }
            SetState(CompanionState.RecoverableError, error is InvalidOperationException ? error.Message : "暫時未能完成回覆；原聲已保存。請結束後再試。");
        }
        finally
        {
            if (playbackId is not null) _archive.EndPlayback(playbackId, _capture?.Position ?? 0, "interrupted");
            lock (_gate) { _buffer.SetLength(0); _buffer.Position = 0; _detector.Reset(); }
        }
    }
    private void EnsureAllowed(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ending) throw new OperationCanceledException(token);
        _archive.EnsureCloudAllowed(_session!.SessionId);
    }
    private async Task WatchPolicyAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, _sessionStop.Token);
                try
                {
                    _archive.EnsureCloudAllowed(_session!.SessionId);
                    if (_capture is not null && !_archive.Repository.HasGrantedConsent(_session.SessionId, ConsentScope.LocalCapture)) throw new ConsentRequiredException();
                }
                catch { _responseStop?.Cancel(); _sessionStop.Cancel(); SetState(CompanionState.RecoverableError, "對話授權已停止。"); _ = Task.Run(EndAsync); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task InterruptAsync()
    {
        _responseStop?.Cancel();
        try { await _backend.CancelAsync(); } catch { }
        await _response;
        if (!_ending && !_sessionStop.IsCancellationRequested) SetState(CompanionState.Listening, "好，你講。");
    }
    public Task EndAsync()
    {
        lock (_gate) { if (_endTask is not null) return _endTask; _ending = true; _endTask = Task.Run(EndCoreAsync); return _endTask; }
    }
    private async Task EndCoreAsync()
    {
        SetState(CompanionState.Ending, "保存緊對話…");
        _sessionStop.Cancel(); _responseStop?.Cancel();
        try { await _backend.CancelAsync(); } catch { }
        await _response; await _policyWatch;
        if (_capture is not null) { _capture.Chunk -= OnAudio; _capture.Failed -= OnCaptureFailure; _capture.Dispose(); _capture = null; }
        if (_session is not null) _archive.Repository.EndSession(_session);
        SetState(CompanionState.Ended, "對話同原聲已保存。");
    }
    private void SetState(CompanionState state, string text)
    {
        lock (_gate) { if (_ending && state is not (CompanionState.Ending or CompanionState.Ended)) return; State = state; }
        StatusChanged?.Invoke(new(state, text));
    }
    public async ValueTask DisposeAsync() { await EndAsync(); await _backend.DisposeAsync(); _buffer.Dispose(); }
}
