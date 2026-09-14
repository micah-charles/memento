using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>Metadata and policy for a live device-backed Realtime session.</summary>
public sealed record RealtimeStreamingRequest(
    string SessionId,
    string? TurnId,
    PrivacyMode PrivacyMode,
    bool LiveCloudConsent,
    DateTimeOffset RequestedAt,
    string? SourceId = null);

public enum RealtimeTurnDetectionMode
{
    Disabled,
    ServerVad,
    SemanticVad
}

/// <summary>
/// Optional protocol configuration for a future automatic-turn session. The
/// shipped app keeps this disabled until live event sequencing and playback
/// truncation are verified on the target device.
/// </summary>
public sealed record RealtimeTurnDetectionOptions(
    RealtimeTurnDetectionMode Mode = RealtimeTurnDetectionMode.Disabled,
    double Threshold = 0.5,
    int PrefixPaddingMs = 300,
    int SilenceDurationMs = 900,
    string Eagerness = "low",
    bool CreateResponse = true,
    bool InterruptResponse = true)
{
    public object? ToPayload()
        => Mode switch
        {
            RealtimeTurnDetectionMode.ServerVad => new
            {
                type = "server_vad",
                threshold = Threshold,
                prefix_padding_ms = PrefixPaddingMs,
                silence_duration_ms = SilenceDurationMs,
                create_response = CreateResponse,
                interrupt_response = InterruptResponse
            },
            RealtimeTurnDetectionMode.SemanticVad => new
            {
                type = "semantic_vad",
                eagerness = Eagerness,
                create_response = CreateResponse,
                interrupt_response = InterruptResponse
            },
            _ => null
        };
}

public interface IRealtimeStreamingProvider
{
    string Provider { get; }
    string Model { get; }
    Task<RealtimeStreamingSession> StartAsync(RealtimeStreamingRequest request, IAudioChunkSource audioSource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Streams PCM chunks from an already-local capture into a Realtime session.
/// The input source remains local and independently owns its archival writer.
/// </summary>
public sealed class OpenAiRealtimeStreamingProvider : IRealtimeStreamingProvider
{
    private const int OutputLimitBytes = 64 * 1024 * 1024;
    private const long InputLimitBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromMinutes(2);
    private readonly IApiCredentialProvider _credentials;
    private readonly Func<IRealtimeMessageTransport> _transportFactory;
    private readonly Uri _endpoint;
    private readonly TimeSpan _completionTimeout;
    private readonly string _transcriptionModel;
    private readonly RealtimeTurnDetectionOptions _turnDetection;

    public OpenAiRealtimeStreamingProvider(
        IApiCredentialProvider credentials,
        string model = "gpt-realtime-2.1-mini",
        Func<IRealtimeMessageTransport>? transportFactory = null,
        Uri? endpoint = null,
        TimeSpan? completionTimeout = null,
        string transcriptionModel = "gpt-transcribe",
        RealtimeTurnDetectionOptions? turnDetection = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
        _transcriptionModel = string.IsNullOrWhiteSpace(transcriptionModel) ? throw new ArgumentException("A realtime transcription model is required.", nameof(transcriptionModel)) : transcriptionModel;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketRealtimeTransport());
        _endpoint = endpoint ?? new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(Model), UriKind.Absolute);
        if (_endpoint.Scheme is not ("wss" or "ws")) throw new ArgumentException("The realtime endpoint must use ws or wss.", nameof(endpoint));
        _completionTimeout = completionTimeout ?? DefaultCompletionTimeout;
        if (_completionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(completionTimeout), "Realtime completion timeout must be positive.");
        _turnDetection = turnDetection ?? new RealtimeTurnDetectionOptions();
    }

    public string Provider => "openai";
    public string Model { get; }
    public string TranscriptionModel => _transcriptionModel;

    public async Task<RealtimeStreamingSession> StartAsync(
        RealtimeStreamingRequest request,
        IAudioChunkSource audioSource,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request, audioSource);
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");

        var transport = _transportFactory() ?? throw new InvalidOperationException("The realtime transport factory returned null.");
        try
        {
            await transport.ConnectAsync(_endpoint, apiKey, cancellationToken).ConfigureAwait(false);
            await transport.SendAsync(JsonSerializer.Serialize(new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime",
                    model = Model,
                    output_modalities = new[] { "audio" },
                    audio = new
                    {
                        input = new
                        {
                            format = new { type = "audio/pcm", rate = 24000 },
                            turn_detection = _turnDetection.ToPayload(),
                            transcription = new { model = _transcriptionModel }
                        },
                        output = new { format = new { type = "audio/pcm", rate = 24000 } }
                    }
                }
            }), cancellationToken).ConfigureAwait(false);
            return RealtimeStreamingSession.Create(transport, audioSource, request, Provider, Model, _completionTimeout);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateRequest(RealtimeStreamingRequest request, IAudioChunkSource audioSource)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("A session ID is required.", nameof(request));
        if (CloudNotPermittedException.IsBlocked(request.PrivacyMode)) throw new CloudNotPermittedException();
        if (!request.LiveCloudConsent) throw new CloudConsentRequiredException();
        ArgumentNullException.ThrowIfNull(audioSource);
        var format = audioSource.Format;
        format.Validate();
        if (format.BitsPerSample != 16 || format.Channels != 1 || format.SampleRate is not (24000 or 48000))
            throw new InvalidDataException("Realtime input requires mono 16-bit PCM at 24 kHz or 48 kHz.");
    }
}

/// <summary>Applies archive consent and Source policy around live streaming.</summary>
public sealed class RealtimeStreamingOrchestrator
{
    private readonly ArchiveRepository _repository;
    private readonly IRealtimeStreamingProvider _provider;
    private readonly DerivedAudioStore? _speechStore;

    public RealtimeStreamingOrchestrator(ArchiveRepository repository, IRealtimeStreamingProvider provider, DerivedAudioStore? speechStore = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _speechStore = speechStore;
    }

    public async Task<RealtimeStreamingArchiveSession> StartAsync(RealtimeStreamingRequest request, IAudioChunkSource audioSource, CancellationToken cancellationToken = default)
    {
        var session = ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(audioSource);
        RealtimeStreamingSession? streaming = null;
        try
        {
            streaming = await _provider.StartAsync(request, audioSource, cancellationToken).ConfigureAwait(false);
            // Consent or Source withdrawal can change while a transport is
            // handshaking. Do not return a live session unless the policy is
            // still valid after the provider has accepted the connection.
            if (!_repository.HasGrantedConsent(session.SessionId, ConsentScope.LiveCloudConversation))
                throw new CloudNotPermittedException();
            EnsureSource(request);
            return new RealtimeStreamingArchiveSession(_repository, _provider, _speechStore, request, streaming);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (streaming is not null)
            {
                try { await streaming.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        }
        catch (Exception error)
        {
            if (streaming is not null)
            {
                try { await streaming.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            AddFailure(request, error);
            throw;
        }
    }

    private Session ValidateRequest(RealtimeStreamingRequest request)
    {
        var session = _repository.GetSession(request.SessionId) ?? throw new InvalidDataException("The requested session was not found.");
        if (session.PrivacyMode != request.PrivacyMode) throw new InvalidDataException("The requested privacy mode does not match the persisted session.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode)) throw new CloudNotPermittedException();
        if (!request.LiveCloudConsent) throw new CloudConsentRequiredException();
        if (!_repository.HasGrantedConsent(session.SessionId, ConsentScope.LiveCloudConversation)) throw new CloudNotPermittedException();
        EnsureSource(request);
        return session;
    }

    internal void EnsureSource(RealtimeStreamingRequest request)
    {
        if (request.SourceId is null) return;
        var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was not found.");
        if (!string.Equals(source.SessionId, request.SessionId, StringComparison.Ordinal)) throw new InvalidDataException("The requested Source does not belong to the requested session.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }

    internal void AddFailure(RealtimeStreamingRequest request, Exception error)
        => _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _provider.Provider, "realtime_conversation", _provider.Model, null, null, request.RequestedAt, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
}

public sealed class RealtimeStreamingArchiveSession : IAsyncDisposable
{
    private readonly ArchiveRepository _repository;
    private readonly IRealtimeStreamingProvider _provider;
    private readonly DerivedAudioStore? _speechStore;
    private readonly RealtimeStreamingRequest _request;
    private readonly RealtimeStreamingSession _streaming;
    private Task<RealtimeConversationResult>? _completion;

    internal RealtimeStreamingArchiveSession(ArchiveRepository repository, IRealtimeStreamingProvider provider, DerivedAudioStore? speechStore, RealtimeStreamingRequest request, RealtimeStreamingSession streaming)
    {
        _repository = repository;
        _provider = provider;
        _speechStore = speechStore;
        _request = request;
        _streaming = streaming;
    }

    public Task<RealtimeConversationResult> CompleteAsync(CancellationToken cancellationToken = default)
        => LazyCompleteAsync(cancellationToken);

    private Task<RealtimeConversationResult> LazyCompleteAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _completion);
        if (existing is not null) return existing.WaitAsync(cancellationToken);
        var created = CompleteCoreAsync(cancellationToken);
        var winner = Interlocked.CompareExchange(ref _completion, created, null);
        return winner ?? created;
    }

    private async Task<RealtimeConversationResult> CompleteCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _streaming.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (!_repository.HasGrantedConsent(_request.SessionId, ConsentScope.LiveCloudConversation)) throw new CloudNotPermittedException();
            EnsureSourceStillAvailable();
            DerivedSpeechAsset? asset = null;
            if (result.OutputAudioPcm.Length > 0 && _speechStore is not null)
            {
                asset = _speechStore.StorePcm(_request.SessionId, _request.TurnId, new PcmWaveFormat(24000, 1, 16), result.OutputAudioPcm, result.Response.Provider, result.Response.Model, result.Response.RequestId, result.Response.CompletedAt);
                if (!_repository.HasGrantedConsent(_request.SessionId, ConsentScope.LiveCloudConversation))
                {
                    DeleteDerivedAsset(asset);
                    throw new CloudNotPermittedException();
                }
                EnsureSourceStillAvailable();
            }
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), _request.SessionId, _request.TurnId, result.Response.Provider, result.Response.Capability, result.Response.Model, result.Response.ModelSnapshot, result.Response.RequestId, _request.RequestedAt, result.Response.CompletedAt, result.Response.InputAudioMs, result.Response.OutputAudioMs, true, null, null, DateTimeOffset.UtcNow));
            return result with { OutputSpeechAsset = asset };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), _request.SessionId, _request.TurnId, _provider.Provider, "realtime_conversation", _provider.Model, null, null, _request.RequestedAt, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
            throw;
        }
    }

    private void EnsureSourceStillAvailable()
    {
        if (_request.SourceId is null) return;
        var source = _repository.GetSource(_request.SourceId) ?? throw new InvalidDataException("The requested Source was removed while realtime was running.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }

    private void DeleteDerivedAsset(DerivedSpeechAsset asset)
    {
        try
        {
            _speechStore?.Remove(asset);
        }
        catch
        {
        }
    }

    public ValueTask DisposeAsync() => _streaming.DisposeAsync();
}

/// <summary>
/// A single live Realtime input session. Call <see cref="CompleteAsync"/>
/// after the participant stops speaking; the output remains a derived result
/// and is never participant evidence by itself.
/// </summary>
public sealed class RealtimeStreamingSession : IAsyncDisposable
{
    private const int OutputLimitBytes = 64 * 1024 * 1024;
    private const long InputLimitBytes = 256L * 1024 * 1024;
    private readonly IRealtimeMessageTransport _transport;
    private readonly IAudioChunkSource _audioSource;
    private readonly RealtimeStreamingRequest _request;
    private readonly string _provider;
    private readonly string _model;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _internalCancellation = new();
    private readonly TaskCompletionSource<RealtimeConversationResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _outputText = new();
    private readonly StringBuilder _outputAudioTranscript = new();
    private readonly StringBuilder _inputTranscript = new();
    private readonly MemoryStream _outputAudio = new();
    private readonly PcmRealtimeEncoder _encoder;
    private readonly TimeSpan _completionTimeout;
    private Task? _sender;
    private Task? _receiver;
    private long _inputBytes;
    private long _requestEventSequence;
    private int _completionStarted;
    private bool _disposed;

    private RealtimeStreamingSession(IRealtimeMessageTransport transport, IAudioChunkSource audioSource, RealtimeStreamingRequest request, string provider, string model, TimeSpan completionTimeout)
    {
        _transport = transport;
        _audioSource = audioSource;
        _request = request;
        _provider = provider;
        _model = model;
        _completionTimeout = completionTimeout;
        _encoder = new PcmRealtimeEncoder(audioSource.Format);
    }

    internal static RealtimeStreamingSession Create(IRealtimeMessageTransport transport, IAudioChunkSource audioSource, RealtimeStreamingRequest request, string provider, string model, TimeSpan completionTimeout)
    {
        var session = new RealtimeStreamingSession(transport, audioSource, request, provider, model, completionTimeout);
        session.Start();
        return session;
    }

    public Task<RealtimeConversationResult> Completion => _completion.Task;

    private void Start()
    {
        _audioSource.DataAvailable += OnDataAvailable;
        _sender = Task.Run(SendChunksAsync);
        _receiver = Task.Run(ReceiveEventsAsync);
    }

    public async Task<RealtimeConversationResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completionStarted, 1) != 0)
            return await WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        _audioSource.DataAvailable -= OnDataAvailable;
        _chunks.Writer.TryComplete();
        try
        {
            await (_sender ?? Task.CompletedTask).ConfigureAwait(false);
            if (_completion.Task.IsCompleted)
                return await WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            _encoder.Complete();
            await _transport.SendAsync("{\"type\":\"input_audio_buffer.commit\"}", cancellationToken).ConfigureAwait(false);
            await _transport.SendAsync("{\"type\":\"response.create\"}", cancellationToken).ConfigureAwait(false);
            return await WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Fail(error);
            throw;
        }
    }

    private async Task<RealtimeConversationResult> WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _completion.Task.WaitAsync(_completionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var bounded = new ProviderRequestException("OpenAI realtime response timed out.", 504);
            Fail(bounded);
            throw bounded;
        }
    }

    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (_disposed || Volatile.Read(ref _completionStarted) != 0) return;
        if (e.BytesRecorded <= 0 || e.BytesRecorded > e.Buffer.Length || e.BytesRecorded % _audioSource.Format.BlockAlign != 0)
        {
            Fail(new InvalidDataException("Realtime audio input contained an incomplete PCM frame."));
            return;
        }
        var totalInput = Interlocked.Add(ref _inputBytes, e.BytesRecorded);
        if (totalInput > InputLimitBytes)
        {
            Fail(new ProviderRequestException("Realtime input exceeded the safety limit.", 413));
            return;
        }
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        if (!_chunks.Writer.TryWrite(copy))
            Fail(new ProviderRequestException("Realtime audio input queue is full.", 429));
    }

    private async Task SendChunksAsync()
    {
        try
        {
            await foreach (var raw in _chunks.Reader.ReadAllAsync(_internalCancellation.Token).ConfigureAwait(false))
            {
                foreach (var encoded in _encoder.Encode(raw))
                {
                    if (encoded.Length == 0) continue;
                    var audio = Convert.ToBase64String(encoded);
                    await _transport.SendAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio }), _internalCancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_internalCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private async Task ReceiveEventsAsync()
    {
        try
        {
            while (true)
            {
                var message = await _transport.ReceiveAsync(_internalCancellation.Token).ConfigureAwait(false);
                if (message is null) throw new ProviderRequestException("OpenAI realtime connection closed before response completion.", 502);
                using var document = ParseMessage(message);
                HandleEvent(document.RootElement);
                if (_completion.Task.IsCompleted) return;
            }
        }
        catch (OperationCanceledException) when (_internalCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private void HandleEvent(JsonElement root)
    {
        var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
        switch (type)
        {
            case "response.output_text.delta":
                AppendString(root, "delta", _outputText);
                break;
            case "response.audio_transcript.delta":
            case "response.output_audio_transcript.delta":
                AppendString(root, "delta", _outputAudioTranscript);
                break;
            case "conversation.item.input_audio_transcription.completed":
                AppendString(root, "transcript", _inputTranscript);
                break;
            case "response.output_audio.delta":
            case "response.audio.delta":
                AppendAudio(root);
                break;
            case "response.done":
                CompleteResponse(root);
                break;
            case "error":
                throw new ProviderRequestException("OpenAI realtime request failed.", 502);
        }
    }

    private void CompleteResponse(JsonElement root)
    {
        var responseId = (string?)null;
        var status = "completed";
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            responseId = response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            status = response.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() ?? status : status;
        }
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new ProviderRequestException("OpenAI realtime response did not complete.", 502);
        var text = _outputText.Length == 0 ? _outputAudioTranscript.ToString() : _outputText.ToString();
        if (string.IsNullOrWhiteSpace(text) && _outputAudio.Length == 0)
            throw new ProviderRequestException("OpenAI realtime response did not contain text or audio.", 502);
        var responseMetadata = new ConversationResponse(
            _provider,
            "realtime_conversation",
            _model,
            null,
            responseId ?? "stream-" + Interlocked.Increment(ref _requestEventSequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            text,
            Interlocked.Read(ref _inputBytes) * 1000L / (_audioSource.Format.ByteRate == 0 ? 1 : _audioSource.Format.ByteRate),
            _outputAudio.Length / 48L,
            DateTimeOffset.UtcNow);
        _completion.TrySetResult(new RealtimeConversationResult(responseMetadata, _outputAudio.ToArray(), _inputTranscript.Length == 0 ? null : _inputTranscript.ToString()));
        _internalCancellation.Cancel();
    }

    private void AppendAudio(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var value) || value.ValueKind != JsonValueKind.String) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.GetString() ?? string.Empty); }
        catch (FormatException) { throw new ProviderRequestException("OpenAI realtime returned invalid audio data.", 502); }
        if (bytes.Length > OutputLimitBytes - _outputAudio.Length)
            throw new ProviderRequestException("OpenAI realtime audio response exceeded the safety limit.", 502);
        _outputAudio.Write(bytes);
    }

    private void Fail(Exception error)
    {
        _completion.TrySetException(error);
        _internalCancellation.Cancel();
        _chunks.Writer.TryComplete(error);
    }

    private static JsonDocument ParseMessage(string message)
    {
        try { return JsonDocument.Parse(message); }
        catch (JsonException) { throw new ProviderRequestException("OpenAI realtime returned malformed event data.", 502); }
    }

    private static void AppendString(JsonElement root, string property, StringBuilder target)
    {
        if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            target.Append(value.GetString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _audioSource.DataAvailable -= OnDataAvailable;
        _chunks.Writer.TryComplete();
        _internalCancellation.Cancel();
        try { if (_sender is not null) await _sender.ConfigureAwait(false); } catch { }
        try { if (_receiver is not null) await _receiver.ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
        _outputAudio.Dispose();
        _internalCancellation.Dispose();
    }

    private sealed class PcmRealtimeEncoder
    {
        private readonly PcmWaveFormat _format;
        private readonly byte[] _pending = new byte[4];
        private int _pendingCount;

        public PcmRealtimeEncoder(PcmWaveFormat format) => _format = format;

        public IReadOnlyList<byte[]> Encode(ReadOnlySpan<byte> raw)
        {
            if (_format.SampleRate == 24000) return [raw.ToArray()];
            var output = new byte[(raw.Length + _pendingCount) / 2];
            var outputCount = 0;
            var offset = 0;
            if (_pendingCount > 0)
            {
                var copied = Math.Min(4 - _pendingCount, raw.Length);
                raw[..copied].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += copied;
                offset += copied;
                if (_pendingCount == 4)
                {
                    _pending.AsSpan(0, 2).CopyTo(output);
                    outputCount = 2;
                    _pendingCount = 0;
                }
            }

            var complete = raw.Length - offset;
            complete -= complete % 4;
            for (var sourceOffset = offset; sourceOffset < offset + complete; sourceOffset += 4)
            {
                raw.Slice(sourceOffset, 2).CopyTo(output.AsSpan(outputCount));
                outputCount += 2;
            }
            var trailing = raw.Length - (offset + complete);
            if (trailing > 0)
            {
                raw.Slice(offset + complete, trailing).CopyTo(_pending);
                _pendingCount = trailing;
            }
            return outputCount == 0 ? [] : [output[..outputCount]];
        }

        public void Complete()
        {
            if (_pendingCount != 0) throw new InvalidDataException("Realtime input ended with an incomplete 48 kHz sample pair.");
        }
    }
}
