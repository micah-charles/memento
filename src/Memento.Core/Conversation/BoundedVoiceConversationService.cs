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
    private readonly ISpeechOutputPlayback? _speechPlayback;
    private readonly bool _queueExtractionJobs;
    private readonly ConversationSessionWriter _sessionWriter;

    public BoundedVoiceConversationService(ArchiveRepository repository, ITranscriptionProvider transcription, IConversationProvider conversation, ISpeechOutputProvider? speechOutput = null, DerivedAudioStore? speechStore = null, ISpeechOutputPlayback? speechPlayback = null, bool queueExtractionJobs = false)
    {
        _repository = repository;
        _transcription = transcription;
        _conversation = new ConversationOrchestrator(repository, conversation);
        _speechOutput = speechOutput;
        _speechStore = speechStore;
        _speechPlayback = speechPlayback;
        _queueExtractionJobs = queueExtractionJobs;
        _sessionWriter = new ConversationSessionWriter(repository);
        if (_speechPlayback is not null && _speechStore is null)
            throw new ArgumentException("Speech playback requires a derived speech store.", nameof(speechPlayback));
    }

    public async Task<BoundedVoiceConversationResult> ExecuteAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        var session = _repository.GetSession(request.SessionId) ?? throw new InvalidDataException("The requested session was not found.");
        if (request.PrivacyMode != session.PrivacyMode) throw new InvalidDataException("The requested privacy mode does not match the persisted session.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode)) throw new CloudNotPermittedException();
        if (!request.CloudConsent) throw new CloudConsentRequiredException();
        if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription)) throw new CloudNotPermittedException();
        if (!File.Exists(request.LocalAudioPath)) throw new FileNotFoundException("Local audio source is required before cloud processing.", request.LocalAudioPath);
        if (string.IsNullOrWhiteSpace(request.SourceId)) throw new InvalidOperationException("A Source ID is required for durable transcription provenance.");
        var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was not found.");
        if (!string.Equals(source.SessionId, request.SessionId, StringComparison.Ordinal)) throw new InvalidDataException("The requested Source does not belong to the requested session.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        SourcePathGuard.EnsureMatches(source, request.LocalAudioPath);
        var started = DateTimeOffset.UtcNow;
        TranscriptionResult transcript;
        try
        {
            transcript = await _transcription.TranscribeAsync(request.LocalAudioPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            var currentSourceAfterTranscription = _repository.GetSource(request.SourceId!) ?? throw new InvalidDataException("The requested Source was removed while transcription was running.");
            if (string.Equals(currentSourceAfterTranscription.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
            if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription)) throw new CloudNotPermittedException();
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, transcript.Provider, "transcription", transcript.Model, null, transcript.RequestId, started, transcript.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
            var revision = _repository.ListTranscriptRevisions(request.SourceId).OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
            if (revision is null)
            {
                revision = _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), request.SourceId, request.TurnId, 1, "initial", transcript.Text, null, null, transcript.CompletedAt));
            }
            if (_queueExtractionJobs)
                _sessionWriter.QueueExtractionIfNeeded(request.SessionId, request.TurnId, request.SourceId, revision.TranscriptRevisionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _transcription.Provider, "transcription", _transcription.Model, null, null, started, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
            if (request.SourceId is not null && IsRetryableProviderFailure(error))
            {
                var retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                QueueRetryIfMissing(request.SessionId, request.TurnId, request.SourceId, "durable_transcription", "transcription provider unavailable: " + error.GetType().Name, retryAt);
            }
            throw;
        }

        if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription)) throw new CloudNotPermittedException();
        var currentSource = _repository.GetSource(request.SourceId!) ?? throw new InvalidDataException("The requested Source was removed while transcription was running.");
        if (string.Equals(currentSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase)) throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        var responseRequest = request with { TranscriptText = transcript.Text };
        var conversation = await _conversation.ExecuteAsync(responseRequest, cancellationToken).ConfigureAwait(false);
        if (conversation.Response is null && request.SourceId is not null && conversation.Interaction is not null && IsRetryableProviderFailure(conversation.Interaction.ErrorCode))
        {
            var retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            QueueRetryIfMissing(request.SessionId, request.TurnId, request.SourceId, "durable_response", "conversation provider unavailable: " + conversation.Interaction.ErrorCode, retryAt);
        }
        SpeechOutputResult? speech = null;
        DerivedSpeechAsset? speechAsset = null;
        if (_speechOutput is not null && conversation.Response is not null)
        {
            if (!_repository.HasGrantedConsent(request.SessionId, ConsentScope.CloudTranscription)) throw new CloudNotPermittedException();
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
                _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, _speechOutput.Provider, "speech_output", _speechOutput.Model, null, null, speechStarted, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, ProviderFailureSummary.ForPersistence(error), DateTimeOffset.UtcNow));
                throw;
            }

            EnsureCloudConsent(request.SessionId);
            EnsureSourceStillAvailable(request);
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), request.SessionId, request.TurnId, speech.Provider, "speech_output", speech.Model, null, speech.RequestId, speechStarted, speech.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
            if (_speechStore is not null)
            {
                speechAsset = _speechStore.Store(request.SessionId, request.TurnId, speech);
                if (_speechPlayback is not null)
                {
                    EnsureCloudConsent(request.SessionId);
                    EnsureSourceStillAvailable(request);
                    await _speechPlayback.PlayAsync(speechAsset, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new BoundedVoiceConversationResult(transcript, conversation, speech, speechAsset);
    }

    private static bool IsRetryableProviderFailure(Exception error)
        => error is ProviderRequestException or HttpRequestException or TaskCanceledException;

    private static bool IsRetryableProviderFailure(string? errorCode)
        => string.Equals(errorCode, nameof(ProviderRequestException), StringComparison.Ordinal) || string.Equals(errorCode, nameof(HttpRequestException), StringComparison.Ordinal) || string.Equals(errorCode, nameof(TaskCanceledException), StringComparison.Ordinal);

    private void QueueRetryIfMissing(string sessionId, string? turnId, string sourceId, string jobType, string error, DateTimeOffset retryAt)
    {
        var now = DateTimeOffset.UtcNow;
        _repository.TryAddConversationJobIfMissing(new ConversationJob(Guid.NewGuid().ToString("N"), sessionId, turnId, sourceId, jobType, ConversationJobStatus.Failed, 0, retryAt, error, now, now));
    }

    private void EnsureSourceStillAvailable(ConversationRequest request)
    {
        if (request.SourceId is null) return;
        var source = _repository.GetSource(request.SourceId) ?? throw new InvalidDataException("The requested Source was removed while speech output was running.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
    }

    private void EnsureCloudConsent(string sessionId)
    {
        if (!_repository.HasGrantedConsent(sessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
    }
}
