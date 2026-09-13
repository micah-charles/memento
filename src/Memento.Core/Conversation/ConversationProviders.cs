using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public sealed record ConversationRequest(
    string SessionId,
    string? TurnId,
    string LocalAudioPath,
    PrivacyMode PrivacyMode,
    bool CloudConsent,
    DateTimeOffset RequestedAt,
    string? TranscriptText = null,
    string? SourceId = null);

public sealed record ConversationResponse(
    string Provider,
    string Capability,
    string Model,
    string? ModelSnapshot,
    string? RequestId,
    string Text,
    long? InputAudioMs,
    long? OutputAudioMs,
    DateTimeOffset CompletedAt);

public sealed class CloudConsentRequiredException() : InvalidOperationException("Cloud conversation requires explicit cloud consent.");

public sealed class CloudNotPermittedException(string? message = null) : InvalidOperationException(message ?? DefaultMessage)
{
    public const string DefaultMessage = "Cloud processing is disabled for LOCAL_CAPTURE_ONLY sessions.";
    public const string WithdrawnSourceMessage = "Cloud processing is disabled because this Source was withdrawn.";
}

public interface IConversationProvider
{
    string Provider { get; }
    string Model { get; }
    Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default);
}

public interface ITranscriptionProvider
{
    string Provider { get; }
    string Model { get; }
    Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default);
}

public interface IReasoningProvider
{
    string Provider { get; }
    string Model { get; }
}

/// <summary>
/// Deterministic provider used for contract tests and offline development. It never claims to be a live cloud call.
/// </summary>
public sealed class DeterministicConversationProvider : IConversationProvider
{
    public string Provider => "deterministic-test";
    public string Model => "fake-voice-v1";

    public Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.LocalAudioPath))
            throw new FileNotFoundException("Local audio source is required before provider transmission.", request.LocalAudioPath);

        return Task.FromResult(new ConversationResponse(
            Provider,
            "conversation",
            Model,
            "fake-voice-v1",
            "fake-" + Guid.NewGuid().ToString("N"),
            "（測試回覆）我已經收到你嘅錄音。",
            null,
            null,
            DateTimeOffset.UtcNow));
    }
}

public sealed record ConversationExecution(
    bool CloudAttempted,
    ConversationResponse? Response,
    ProviderInteraction? Interaction,
    string? Failure);

public sealed class ConversationOrchestrator
{
    private readonly ArchiveRepository _repository;
    private readonly IConversationProvider _provider;

    public ConversationOrchestrator(ArchiveRepository repository, IConversationProvider provider)
    {
        _repository = repository;
        _provider = provider;
    }

    public async Task<ConversationExecution> ExecuteAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PrivacyMode == PrivacyMode.LocalCaptureOnly)
            throw new CloudNotPermittedException();
        if (!request.CloudConsent)
            throw new CloudConsentRequiredException();
        if (!File.Exists(request.LocalAudioPath))
            throw new FileNotFoundException("Local audio source is required before provider transmission.", request.LocalAudioPath);
        if (request.SourceId is not null)
        {
            var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was not found.");
            if (!string.Equals(source.SessionId, request.SessionId, StringComparison.Ordinal))
                throw new InvalidDataException("The requested Source does not belong to the requested session.");
            if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
                throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        }

        var started = DateTimeOffset.UtcNow;
        ConversationResponse response;
        try
        {
            response = await _provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var interaction = _repository.AddProviderInteraction(new ProviderInteraction(
                Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _provider.Provider, "conversation",
                _provider.Model, null, null, started, DateTimeOffset.UtcNow, null, null, false,
                error.GetType().Name, error.Message, DateTimeOffset.UtcNow));
            return new ConversationExecution(true, null, interaction, error.Message);
        }

        // A Source can be withdrawn while a provider request is in flight. Do
        // not persist the response metadata or hand the response to a later
        // speech/extraction stage after that policy change.
        EnsureSourceStillAvailable(request);
        var successfulInteraction = _repository.AddProviderInteraction(new ProviderInteraction(
            Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, response.Provider, response.Capability,
            response.Model, response.ModelSnapshot, response.RequestId, started, response.CompletedAt,
            response.InputAudioMs, response.OutputAudioMs, true, null, null, DateTimeOffset.UtcNow));
        return new ConversationExecution(true, response, successfulInteraction, null);
    }

    private void EnsureSourceStillAvailable(ConversationRequest request)
    {
        if (request.SourceId is null) return;
        var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was removed while conversation was running.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }
}
