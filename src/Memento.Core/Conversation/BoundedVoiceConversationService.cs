using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public sealed record BoundedVoiceConversationResult(TranscriptionResult Transcription, ConversationExecution Conversation);

/// <summary>Turn-based fallback pipeline: local finalized audio, durable transcription, then a bounded response.</summary>
public sealed class BoundedVoiceConversationService
{
    private readonly ArchiveRepository _repository;
    private readonly ITranscriptionProvider _transcription;
    private readonly ConversationOrchestrator _conversation;

    public BoundedVoiceConversationService(ArchiveRepository repository, ITranscriptionProvider transcription, IConversationProvider conversation)
    {
        _repository = repository;
        _transcription = transcription;
        _conversation = new ConversationOrchestrator(repository, conversation);
    }

    public async Task<BoundedVoiceConversationResult> ExecuteAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PrivacyMode == PrivacyMode.LocalCaptureOnly) throw new CloudNotPermittedException();
        if (!request.CloudConsent) throw new CloudConsentRequiredException();
        if (!File.Exists(request.LocalAudioPath)) throw new FileNotFoundException("Local audio source is required before cloud processing.", request.LocalAudioPath);
        var started = DateTimeOffset.UtcNow;
        TranscriptionResult transcript;
        try
        {
            transcript = await _transcription.TranscribeAsync(request.LocalAudioPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, transcript.Provider, "transcription", transcript.Model, null, transcript.RequestId, started, transcript.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _transcription.Provider, "transcription", _transcription.Model, null, null, started, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, error.Message, DateTimeOffset.UtcNow));
            throw;
        }

        var responseRequest = request with { TranscriptText = transcript.Text };
        var conversation = await _conversation.ExecuteAsync(responseRequest, cancellationToken).ConfigureAwait(false);
        return new BoundedVoiceConversationResult(transcript, conversation);
    }
}
