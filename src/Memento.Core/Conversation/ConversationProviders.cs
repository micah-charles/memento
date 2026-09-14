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

internal static class SourcePathGuard
{
    public static void EnsureMatches(SourceMetadata source, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(source.FilePath) || !PathsEqual(source.FilePath, requestedPath))
            throw new InvalidDataException("The requested audio path does not match the archived Source.");
        if (!File.Exists(source.FilePath))
            throw new FileNotFoundException("The archived Source audio file was not found.", source.FilePath);

        var fileInfo = new FileInfo(source.FilePath);
        if (source.ByteLength is long expectedLength && fileInfo.Length != expectedLength)
            throw new InvalidDataException("The Source audio length does not match its archived metadata.");
        if (IsSha256(source.Sha256))
        {
            using var stream = new FileStream(source.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actualHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Source audio failed its archived SHA-256 integrity check.");
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}

/// <summary>Produces bounded, content-free diagnostics for persisted provider failures.</summary>
public static class ProviderFailureSummary
{
    public static string ForPersistence(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            ProviderRequestException request => $"provider request failed (HTTP {request.StatusCode})",
            HttpRequestException => "network request failed",
            TaskCanceledException => "provider request timed out or was cancelled",
            CloudNotPermittedException => "cloud processing was not permitted",
            CloudConsentRequiredException => "cloud consent was required",
            _ => error.GetType().Name
        };
    }
}

public sealed class CloudNotPermittedException(string? message = null) : InvalidOperationException(message ?? DefaultMessage)
{
    public const string DefaultMessage = "Cloud processing is disabled for PRIVATE_CONVERSATION and LOCAL_CAPTURE_ONLY sessions.";
    public const string WithdrawnSourceMessage = "Cloud processing is disabled because this Source was withdrawn.";

    public static bool IsBlocked(PrivacyMode privacyMode)
        => privacyMode is PrivacyMode.PrivateConversation or PrivacyMode.LocalCaptureOnly;
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
        var session = _repository.GetSession(request.SessionId) ?? throw new InvalidDataException("The requested session was not found.");
        if (request.PrivacyMode != session.PrivacyMode)
            throw new InvalidDataException("The requested privacy mode does not match the persisted session.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode))
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
            SourcePathGuard.EnsureMatches(source, request.LocalAudioPath);
        }
        if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();

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
                error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
            return new ConversationExecution(true, null, interaction, ProviderFailureSummary.ForPersistence(error));
        }

        // A Source can be withdrawn while a provider request is in flight. Do
        // not persist the response metadata or hand the response to a later
        // speech/extraction stage after that policy change.
        if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
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
