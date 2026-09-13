using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>Turns a queued local audio job into an append-only initial transcript revision.</summary>
public sealed class DurableTranscriptionJobProcessor : IConversationJobProcessor
{
    private readonly ArchiveRepository _repository;
    private readonly ITranscriptionProvider _provider;
    private readonly string? _languageHint;

    public DurableTranscriptionJobProcessor(ArchiveRepository repository, ITranscriptionProvider provider, string? languageHint = null)
    {
        _repository = repository;
        _provider = provider;
        _languageHint = languageHint;
    }

    public async Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
    {
        if (job.JobType != "durable_transcription") throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}");
        var session = _repository.GetSession(job.SessionId) ?? throw new InvalidOperationException("The queued session was not found.");
        if (session.PrivacyMode == PrivacyMode.LocalCaptureOnly || !_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        if (_repository.ListTranscriptRevisions(job.SourceId).Count > 0) return;
        var sourcePath = _repository.GetSourceFilePath(job.SourceId);
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new FileNotFoundException("The queued Source has no local file path.", job.SourceId);
        var result = await _provider.TranscribeAsync(sourcePath, _languageHint, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidDataException("Transcription provider returned no text.");
        _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), job.SourceId, job.TurnId, 1, "initial", result.Text, null, null, result.CompletedAt));
    }

}
