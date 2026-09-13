using Memento.Core.Conversation;
using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Memory;

/// <summary>Processes one persisted transcript into reviewable candidate records after re-checking cloud consent.</summary>
public sealed class DurableMemoryExtractionJobProcessor : IConversationJobProcessor
{
    private readonly ArchiveRepository _repository;
    private readonly AsyncMemoryExtractionService _service;

    public DurableMemoryExtractionJobProcessor(ArchiveRepository repository, IAsyncMemoryExtractionProvider provider)
    {
        _repository = repository;
        _service = new AsyncMemoryExtractionService(repository, provider);
    }

    public async Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
    {
        if (job.JobType != "durable_extraction") throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}");
        var session = _repository.GetSession(job.SessionId) ?? throw new InvalidOperationException("The queued session was not found.");
        if (session.PrivacyMode == PrivacyMode.LocalCaptureOnly || !_repository.HasGrantedConsent(job.SessionId, ConsentScope.CloudTranscription))
            throw new CloudNotPermittedException();
        var source = _repository.GetSource(job.SourceId) ?? throw new InvalidDataException("The queued Source was not found.");
        var revisions = _repository.ListTranscriptRevisions(job.SourceId);
        var revision = job.TranscriptRevisionId is null
            ? revisions.OrderByDescending(item => item.RevisionNumber).FirstOrDefault()
            : revisions.FirstOrDefault(item => item.TranscriptRevisionId == job.TranscriptRevisionId);
        if (revision is null) throw new InvalidDataException("The queued Source has no matching transcript revision.");
        await _service.ExtractAndPersistAsync(session, source, revision, cancellationToken).ConfigureAwait(false);
    }
}
