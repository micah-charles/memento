using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public interface IConversationJobProcessor
{
    Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default);
}

public sealed record ConversationWorkerRunResult(int Examined, int Succeeded, int Failed, IReadOnlyList<string> Errors);

/// <summary>One bounded, restartable pass over locally persisted conversation work.</summary>
public sealed class ConversationJobWorker
{
    private readonly ArchiveRepository _repository;
    private readonly ConversationSessionWriter _writer;
    private readonly IConversationJobProcessor _processor;
    private readonly TimeSpan _baseRetryDelay;

    public ConversationJobWorker(ArchiveRepository repository, IConversationJobProcessor processor, TimeSpan? baseRetryDelay = null)
    {
        _repository = repository;
        _writer = new ConversationSessionWriter(repository);
        _processor = processor;
        _baseRetryDelay = baseRetryDelay ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ConversationWorkerRunResult> RunOnceAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var jobs = _repository.ListRetryableConversationJobs(clock);
        var errors = new List<string>();
        var succeeded = 0;
        var failed = 0;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processing = _writer.BeginAttempt(job, clock);
            try
            {
                await _processor.ProcessAsync(processing, cancellationToken).ConfigureAwait(false);
                _writer.MarkSucceeded(processing, clock);
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                var exponent = Math.Min(processing.AttemptCount - 1, 8);
                var retryAt = clock + TimeSpan.FromMilliseconds(Math.Min(_baseRetryDelay.TotalMilliseconds * Math.Pow(2, exponent), TimeSpan.FromHours(1).TotalMilliseconds));
                _writer.MarkFailed(processing, error.Message, retryAt, clock);
                errors.Add(error.Message);
                failed++;
            }
        }

        return new ConversationWorkerRunResult(jobs.Count, succeeded, failed, errors);
    }

    public async Task RunUntilCancelledAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunOnceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
