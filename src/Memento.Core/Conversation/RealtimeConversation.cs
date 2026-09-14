using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Memento.Core.Audio;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public sealed record RealtimeConversationRequest(
    string SessionId,
    string? TurnId,
    string LocalAudioPath,
    PrivacyMode PrivacyMode,
    bool LiveCloudConsent,
    DateTimeOffset RequestedAt,
    string? SourceId = null);

public sealed record RealtimeConversationResult(
    ConversationResponse Response,
    byte[] OutputAudioPcm,
    string? InputTranscript,
    DerivedSpeechAsset? OutputSpeechAsset = null);

public interface IRealtimeConversationProvider
{
    string Provider { get; }
    string Model { get; }
    Task<RealtimeConversationResult> SendAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Minimal text-message transport boundary for a Realtime WebSocket.</summary>
public interface IRealtimeMessageTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken = default);
    Task SendAsync(string message, CancellationToken cancellationToken = default);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>System WebSocket implementation used by the optional OpenAI Realtime adapter.</summary>
public sealed class ClientWebSocketRealtimeTransport : IRealtimeMessageTransport
{
    private ClientWebSocket? _socket;

    public async Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("An API credential is required.", nameof(apiKey));
        if (_socket is not null) throw new InvalidOperationException("The realtime transport is already connected.");

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            _socket = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new InvalidOperationException("The realtime transport is not connected.");
        var bytes = Encoding.UTF8.GetBytes(message ?? throw new ArgumentNullException(nameof(message)));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null || (_socket.State is not WebSocketState.Open and not WebSocketState.CloseReceived))
            throw new InvalidOperationException("The realtime transport is not connected.");

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("The realtime transport returned a non-text control message.");
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    public async ValueTask DisposeAsync()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "MEMENTO finished", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Dispose remains best effort during cancellation or network loss.
        }
        finally
        {
            socket.Dispose();
        }
    }
}

/// <summary>
/// Optional completed-turn Realtime adapter. It sends only PCM payload from a
/// finalized local WAV and keeps the transport injectable for deterministic
/// protocol tests. Durable transcription remains a separate archive step.
/// </summary>
public sealed class OpenAiRealtimeWebSocketProvider : IRealtimeConversationProvider
{
    private const int InputChunkBytes = 256 * 1024;
    private const int OutputLimitBytes = 64 * 1024 * 1024;
    private readonly IApiCredentialProvider _credentials;
    private readonly Func<IRealtimeMessageTransport> _transportFactory;
    private readonly Uri _endpoint;

    public OpenAiRealtimeWebSocketProvider(
        IApiCredentialProvider credentials,
        string model = "gpt-realtime-2.1-mini",
        Func<IRealtimeMessageTransport>? transportFactory = null,
        Uri? endpoint = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketRealtimeTransport());
        _endpoint = endpoint ?? new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(Model), UriKind.Absolute);
        if (_endpoint.Scheme is not ("wss" or "ws")) throw new ArgumentException("The realtime endpoint must use ws or wss.", nameof(endpoint));
    }

    public string Provider => "openai";
    public string Model { get; }

    public async Task<RealtimeConversationResult> SendAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");

        var format = PcmWaveValidator.Validate(request.LocalAudioPath, allowPartial: false);
        if (format.BitsPerSample != 16 || format.Channels != 1)
            throw new InvalidDataException("Realtime input requires mono 16-bit PCM WAV audio.");
        if (format.SampleRate is not (24000 or 48000))
            throw new InvalidDataException("Realtime input requires 24 kHz or 48 kHz PCM WAV audio.");
        var dataBytes = PcmWaveValidator.GetDataBytes(request.LocalAudioPath);
        if (dataBytes <= 0) throw new InvalidDataException("Realtime input audio is empty.");

        await using var transport = _transportFactory() ?? throw new InvalidOperationException("The realtime transport factory returned null.");
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
                        format = new { type = "audio/pcm", rate = format.SampleRate },
                        turn_detection = (object?)null,
                        transcription = new { model = "gpt-transcribe" }
                    },
                    output = new { format = new { type = "audio/pcm", rate = 24000 } }
                }
            }
        }), cancellationToken).ConfigureAwait(false);

        await SendAudioAsync(transport, request.LocalAudioPath, dataBytes, format, cancellationToken).ConfigureAwait(false);
        await transport.SendAsync("{\"type\":\"input_audio_buffer.commit\"}", cancellationToken).ConfigureAwait(false);
        await transport.SendAsync("{\"type\":\"response.create\"}", cancellationToken).ConfigureAwait(false);

        var outputText = new StringBuilder();
        var outputAudioTranscript = new StringBuilder();
        var inputTranscript = new StringBuilder();
        using var output = new MemoryStream();
        string? requestId = null;
        string? responseId = null;
        var completedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            var message = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is null) throw new ProviderRequestException("OpenAI realtime connection closed before response completion.", 502);
            using var document = ParseMessage(message);
            var root = document.RootElement;
            if (root.TryGetProperty("event_id", out var eventId) && eventId.ValueKind == JsonValueKind.String)
                requestId ??= eventId.GetString();
            var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            switch (type)
            {
                case "response.output_text.delta":
                    AppendString(root, "delta", outputText);
                    break;
                case "response.audio_transcript.delta":
                case "response.output_audio_transcript.delta":
                    AppendString(root, "delta", outputAudioTranscript);
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    AppendString(root, "transcript", inputTranscript);
                    break;
                case "response.output_audio.delta":
                case "response.audio.delta":
                    AppendAudio(root, output);
                    break;
                case "response.done":
                    if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                    {
                        responseId = response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : responseId;
                        var status = response.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String ? statusElement.GetString() : "completed";
                        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                            throw new ProviderRequestException("OpenAI realtime response did not complete.", 502);
                    }
                    completedAt = DateTimeOffset.UtcNow;
                    var responseText = outputText.Length == 0 ? outputAudioTranscript.ToString() : outputText.ToString();
                    if (string.IsNullOrWhiteSpace(responseText) && output.Length == 0)
                        throw new ProviderRequestException("OpenAI realtime response did not contain text or audio.", 502);
                    var responseMetadata = new ConversationResponse(Provider, "realtime_conversation", Model, null, responseId ?? requestId, responseText, dataBytes * 1000L / format.ByteRate, output.Length / 48L, completedAt);
                    return new RealtimeConversationResult(responseMetadata, output.ToArray(), inputTranscript.Length == 0 ? null : inputTranscript.ToString());
                case "error":
                    throw new ProviderRequestException("OpenAI realtime request failed.", 502);
            }
        }
    }

    private static async Task SendAudioAsync(IRealtimeMessageTransport transport, string path, long dataBytes, PcmWaveFormat format, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, InputChunkBytes, FileOptions.SequentialScan);
        stream.Position = 44;
        var buffer = new byte[InputChunkBytes];
        var output = new byte[InputChunkBytes / 2];
        long remaining = dataBytes;
        var pending = new byte[4];
        var pendingCount = 0;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Realtime input WAV ended before its declared data length.");
            remaining -= read;

            if (format.SampleRate == 24000)
            {
                var audio = Convert.ToBase64String(buffer, 0, read);
                await transport.SendAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio }), cancellationToken).ConfigureAwait(false);
                continue;
            }

            // MEMENTO capture defaults to 48 kHz. Realtime GA PCM input is
            // 24 kHz, so downsample by taking one frame from each pair. The
            // input is mono 16-bit, making this a safe integer-ratio path.
            var offset = 0;
            if (pendingCount > 0)
            {
                var needed = 4 - pendingCount;
                var copied = Math.Min(needed, read);
                Buffer.BlockCopy(buffer, 0, pending, pendingCount, copied);
                pendingCount += copied;
                offset += copied;
                if (pendingCount == 4)
                {
                    Buffer.BlockCopy(pending, 0, output, 0, 2);
                    var emitted = 2;
                    pendingCount = 0;
                    await transport.SendAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(output, 0, emitted) }), cancellationToken).ConfigureAwait(false);
                }
            }

            var complete = read - offset;
            complete -= complete % 4;
            var outputOffset = 0;
            for (var sourceOffset = offset; sourceOffset < offset + complete; sourceOffset += 4)
            {
                if (outputOffset + 2 > output.Length)
                {
                    await transport.SendAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(output, 0, outputOffset) }), cancellationToken).ConfigureAwait(false);
                    outputOffset = 0;
                }
                Buffer.BlockCopy(buffer, sourceOffset, output, outputOffset, 2);
                outputOffset += 2;
            }
            if (outputOffset > 0)
                await transport.SendAsync(JsonSerializer.Serialize(new { type = "input_audio_buffer.append", audio = Convert.ToBase64String(output, 0, outputOffset) }), cancellationToken).ConfigureAwait(false);

            var trailing = read - (offset + complete);
            if (trailing > 0)
            {
                Buffer.BlockCopy(buffer, offset + complete, pending, 0, trailing);
                pendingCount = trailing;
            }
        }
        if (pendingCount != 0) throw new InvalidDataException("Realtime input WAV does not contain complete 48 kHz sample pairs.");
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

    private static void AppendAudio(JsonElement root, MemoryStream output)
    {
        if (!root.TryGetProperty("delta", out var value) || value.ValueKind != JsonValueKind.String) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.GetString() ?? string.Empty); }
        catch (FormatException) { throw new ProviderRequestException("OpenAI realtime returned invalid audio data.", 502); }
        if (bytes.Length > OutputLimitBytes - output.Length)
            throw new ProviderRequestException("OpenAI realtime audio response exceeded the safety limit.", 502);
        output.Write(bytes);
    }

    private static void ValidateRequest(RealtimeConversationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("A session ID is required.", nameof(request));
        if (CloudNotPermittedException.IsBlocked(request.PrivacyMode)) throw new CloudNotPermittedException();
        if (!request.LiveCloudConsent) throw new CloudConsentRequiredException();
        if (!File.Exists(request.LocalAudioPath)) throw new FileNotFoundException("Local audio source is required before realtime transmission.", request.LocalAudioPath);
    }
}

/// <summary>Applies archive consent and Source policy around the optional realtime provider.</summary>
public sealed class RealtimeConversationOrchestrator
{
    private readonly ArchiveRepository _repository;
    private readonly IRealtimeConversationProvider _provider;
    private readonly DerivedAudioStore? _speechStore;

    public RealtimeConversationOrchestrator(ArchiveRepository repository, IRealtimeConversationProvider provider, DerivedAudioStore? speechStore = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _speechStore = speechStore;
    }

    public async Task<RealtimeConversationResult> ExecuteAsync(RealtimeConversationRequest request, CancellationToken cancellationToken = default)
    {
        var session = _repository.GetSession(request.SessionId) ?? throw new InvalidDataException("The requested session was not found.");
        if (session.PrivacyMode != request.PrivacyMode) throw new InvalidDataException("The requested privacy mode does not match the persisted session.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode)) throw new CloudNotPermittedException();
        if (!request.LiveCloudConsent) throw new CloudConsentRequiredException();
        if (!_repository.HasGrantedConsent(session.SessionId, ConsentScope.LiveCloudConversation)) throw new CloudNotPermittedException();
        if (request.SourceId is not null)
        {
            var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was not found.");
            if (!string.Equals(source.SessionId, request.SessionId, StringComparison.Ordinal)) throw new InvalidDataException("The requested Source does not belong to the requested session.");
            if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
            SourcePathGuard.EnsureMatches(source, request.LocalAudioPath);
        }

        var started = DateTimeOffset.UtcNow;
        RealtimeConversationResult result;
        try
        {
            result = await _provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _provider.Provider, "realtime_conversation", _provider.Model, null, null, started, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
            throw;
        }

        if (!_repository.HasGrantedConsent(session.SessionId, ConsentScope.LiveCloudConversation)) throw new CloudNotPermittedException();
        EnsureSourceStillAvailable(request);
        DerivedSpeechAsset? outputSpeechAsset = null;
        if (result.OutputAudioPcm.Length > 0 && _speechStore is not null)
        {
            outputSpeechAsset = _speechStore.StorePcm(
                request.SessionId,
                request.TurnId,
                new PcmWaveFormat(24000, 1, 16),
                result.OutputAudioPcm,
                result.Response.Provider,
                result.Response.Model,
                result.Response.RequestId,
                result.Response.CompletedAt);
            try
            {
                if (!_repository.HasGrantedConsent(session.SessionId, ConsentScope.LiveCloudConversation))
                    throw new CloudNotPermittedException();
                EnsureSourceStillAvailable(request);
            }
            catch
            {
                TryDeleteDerivedAsset(outputSpeechAsset);
                throw;
            }
        }
        _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, result.Response.Provider, result.Response.Capability, result.Response.Model, result.Response.ModelSnapshot, result.Response.RequestId, started, result.Response.CompletedAt, result.Response.InputAudioMs, result.Response.OutputAudioMs, true, null, null, DateTimeOffset.UtcNow));
        return result with { OutputSpeechAsset = outputSpeechAsset };
    }

    private void EnsureSourceStillAvailable(RealtimeConversationRequest request)
    {
        if (request.SourceId is null) return;
        var source = _repository.GetSource(request.SourceId)
            ?? throw new InvalidDataException("The requested Source was removed while realtime conversation was running.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }

    private void TryDeleteDerivedAsset(DerivedSpeechAsset asset)
    {
        try
        {
            if (File.Exists(asset.FilePath)) File.Delete(asset.FilePath);
            using var connection = _repository.Archive.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM derived_speech_assets WHERE derived_speech_asset_id = $id";
            command.Parameters.AddWithValue("$id", asset.DerivedSpeechAssetId);
            command.ExecuteNonQuery();
        }
        catch
        {
            // Preserve the policy failure; a later health check can expose any
            // cleanup problem without leaking provider content.
        }
    }
}
