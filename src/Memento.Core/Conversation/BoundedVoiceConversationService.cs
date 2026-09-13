using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public sealed record BoundedVoiceConversationResult(TranscriptionResult Transcription, ConversationExecution Conversation, SpeechOutputResult? SpeechOutput, DerivedSpeechAsset? SpeechAsset = null);

/// <summary>Turn-based fallback pipeline: local finalized audio, durable transcription, then a bounded response.</summary>
public sealed class BoundedVoiceConversationService
{
    private readonly ArchiveRepository _repository;
    private readonly ITranscriptionProvider _transcription;
    private readonly ConversationOrchestrator _conversation;
    private readonly ISpeechOutputProvider? _speechOutput;
    private readonly DerivedAudioStore? _speechStore;

    public BoundedVoiceConversationService(ArchiveRepository repository, ITranscriptionProvider transcription, IConversationProvider conversation, ISpeechOutputProvider? speechOutput = null, DerivedAudioStore? speechStore = null)
    {
        _repository = repository;
        _transcription = transcription;
        _conversation = new ConversationOrchestrator(repository, conversation);
        _speechOutput = speechOutput;
        _speechStore = speechStore;
    }

    public async Task<BoundedVoiceConversationResult> ExecuteAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PrivacyMode == PrivacyMode.LocalCaptureOnly) throw new CloudNotPermittedException();
        if (!request.CloudConsent) throw new CloudConsentRequiredException();
        if (!File.Exists(request.LocalAudioPath)) throw new FileNotFoundException("Local audio source is required before cloud processing.", request.LocalAudioPath);
        if (string.IsNullOrWhiteSpace(request.SourceId)) throw new InvalidOperationException("A Source ID is required for durable transcription provenance.");
        var started = DateTimeOffset.UtcNow;
        TranscriptionResult transcript;
        try
        {
            transcript = await _transcription.TranscribeAsync(request.LocalAudioPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, transcript.Provider, "transcription", transcript.Model, null, transcript.RequestId, started, transcript.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
            if (_repository.ListTranscriptRevisions(request.SourceId).Count == 0)
                _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), request.SourceId, request.TurnId, 1, "initial", transcript.Text, null, null, transcript.CompletedAt));
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
        SpeechOutputResult? speech = null;
        DerivedSpeechAsset? speechAsset = null;
        if (_speechOutput is not null && conversation.Response is not null)
        {
            var speechStarted = DateTimeOffset.UtcNow;
            try
            {
                speech = await _speechOutput.SynthesizeAsync(conversation.Response.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _speechOutput.Provider, "speech_output", _speechOutput.Model, null, null, speechStarted, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, error.Message, DateTimeOffset.UtcNow));
                throw;
            }

            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, speech.Provider, "speech_output", speech.Model, null, speech.RequestId, speechStarted, speech.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
            if (_speechStore is not null)
                speechAsset = _speechStore.Store(request.SessionId, request.TurnId, speech);
        }

        return new BoundedVoiceConversationResult(transcript, conversation, speech, speechAsset);
    }
}
