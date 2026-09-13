using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>Retries a failed bounded text response from a persisted transcript without retransmitting audio.</summary>
public sealed class DurableResponseJobProcessor : IConversationJobProcessor
{
    private readonly ArchiveRepository _repository;
    private readonly IConversationProvider _provider;
    private readonly ISpeechOutputProvider? _speechOutput;
    private readonly DerivedAudioStore? _speechStore;

    public DurableResponseJobProcessor(ArchiveRepository repository, IConversationProvider provider, ISpeechOutputProvider? speechOutput = null, DerivedAudioStore? speechStore = null)
    {
        _repository = repository;
        _provider = provider;
        _speechOutput = speechOutput;
        _speechStore = speechStore;
        if (_speechOutput is not null && _speechStore is null)
            throw new ArgumentException("Speech output retry requires a derived speech store.", nameof(speechStore));
    }

    public async Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
    {
        if (job.JobType != "durable_response") throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}");
        var session = _repository.GetSession(job.SessionId) ?? throw new InvalidOperationException("The queued session was not found.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode) || !_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        var source = _repository.GetSource(job.SourceId) ?? throw new InvalidDataException("The queued Source was not found.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        var sourcePath = source.FilePath;
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new FileNotFoundException("The queued Source has no local file path.", job.SourceId);
        var revision = _repository.ListTranscriptRevisions(job.SourceId).OrderByDescending(item => item.RevisionNumber).FirstOrDefault() ?? throw new InvalidDataException("The queued Source has no transcript revision.");
        var request = new ConversationRequest(session.SessionId, job.TurnId, sourcePath, session.PrivacyMode, true, DateTimeOffset.UtcNow, revision.Text, job.SourceId);
        var execution = await new ConversationOrchestrator(_repository, _provider).ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (execution.Response is null) throw new ProviderRequestException("The response provider did not return a response.", 503);

        if (_speechOutput is null) return;
        var started = DateTimeOffset.UtcNow;
        SpeechOutputResult speech;
        try
        {
            speech = await _speechOutput.SynthesizeAsync(execution.Response.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), job.SessionId, job.TurnId, _speechOutput.Provider, "speech_output", _speechOutput.Model, null, null, started, DateTimeOffset.UtcNow, null, null, false, error.GetType().Name, error.Message, DateTimeOffset.UtcNow));
            throw;
        }

        var currentSource = _repository.GetSource(job.SourceId) ?? throw new InvalidDataException("The queued Source was removed while speech output was running.");
        if (!_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        if (string.Equals(currentSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        _repository.AddProviderInteraction(new ProviderInteraction(Guid.NewGuid().ToString("N"), job.SessionId, job.TurnId, speech.Provider, "speech_output", speech.Model, null, speech.RequestId, started, speech.CompletedAt, null, null, true, null, null, DateTimeOffset.UtcNow));
        _speechStore!.Store(job.SessionId, job.TurnId, speech);
    }
}
