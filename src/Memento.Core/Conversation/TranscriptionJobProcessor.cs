using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

/// <summary>Turns a queued local audio job into an append-only initial transcript revision.</summary>
public sealed class DurableTranscriptionJobProcessor : IConversationJobProcessor
{
    private readonly ArchiveRepository _repository;
    private readonly ITranscriptionProvider _provider;
    private readonly string? _languageHint;
    private readonly bool _queueExtractionJobs;
    private readonly ConversationSessionWriter _sessionWriter;

    public DurableTranscriptionJobProcessor(ArchiveRepository repository, ITranscriptionProvider provider, string? languageHint = null, bool queueExtractionJobs = false)
    {
        _repository = repository;
        _provider = provider;
        _languageHint = languageHint;
        _queueExtractionJobs = queueExtractionJobs;
        _sessionWriter = new ConversationSessionWriter(repository);
    }

    public async Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
    {
        if (job.JobType != "durable_transcription") throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}");
        var session = _repository.GetSession(job.SessionId) ?? throw new InvalidOperationException("The queued session was not found.");
        if (CloudNotPermittedException.IsBlocked(session.PrivacyMode) || !_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        var source = _repository.GetSource(job.SourceId) ?? throw new InvalidDataException("The queued Source was not found.");
        if (string.Equals(source.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        var existingRevision = _repository.ListTranscriptRevisions(job.SourceId).OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        if (existingRevision is not null)
        {
            if (_queueExtractionJobs) _sessionWriter.QueueExtractionIfNeeded(job.SessionId, job.TurnId, job.SourceId, existingRevision.TranscriptRevisionId);
            return;
        }
        var sourcePath = _repository.GetSourceFilePath(job.SourceId);
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new FileNotFoundException("The queued Source has no local file path.", job.SourceId);
        if (Path.IsPathFullyQualified(sourcePath))
            SourcePathGuard.EnsureMatches(source, sourcePath, ArchivePathSafety.GetArchiveRoot(_repository.Archive));
        var result = await _provider.TranscribeAsync(sourcePath, _languageHint, cancellationToken).ConfigureAwait(false);
        var currentSource = _repository.GetSource(job.SourceId) ?? throw new InvalidDataException("The queued Source was removed while transcription was running.");
        if (string.Equals(currentSource.RecoveryStatus, "withdrawn", StringComparison.OrdinalIgnoreCase))
            throw new CloudNotPermittedException(CloudNotPermittedException.WithdrawnSourceMessage);
        if (!_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidDataException("Transcription provider returned no text.");
        // Another worker can finish the same source while this provider call
        // is in flight. Re-check before appending the initial revision so a
        // retry race cannot create duplicate transcript provenance.
        var revisionCreatedByConcurrentWorker = _repository.ListTranscriptRevisions(job.SourceId).OrderByDescending(item => item.RevisionNumber).FirstOrDefault();
        if (revisionCreatedByConcurrentWorker is not null)
        {
            if (_queueExtractionJobs)
                _sessionWriter.QueueExtractionIfNeeded(job.SessionId, job.TurnId, job.SourceId, revisionCreatedByConcurrentWorker.TranscriptRevisionId);
            return;
        }
        _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), job.SourceId, job.TurnId, 1, "initial", result.Text, null, null, result.CompletedAt));
        var revision = _repository.ListTranscriptRevisions(job.SourceId).OrderByDescending(item => item.RevisionNumber).First();
        if (_queueExtractionJobs) _sessionWriter.QueueExtractionIfNeeded(job.SessionId, job.TurnId, job.SourceId, revision.TranscriptRevisionId);
    }

}
