using Memento.Core.Domain;

namespace Memento.Core.Conversation;

/// <summary>Routes persisted conversation jobs to the capability-specific processor.</summary>
public sealed class CompositeConversationJobProcessor : IConversationJobProcessor
{
    private readonly IConversationJobProcessor _transcription;
    private readonly IConversationJobProcessor _response;
    private readonly IConversationJobProcessor? _extraction;

    public CompositeConversationJobProcessor(IConversationJobProcessor transcription, IConversationJobProcessor response, IConversationJobProcessor? extraction = null)
    {
        _transcription = transcription;
        _response = response;
        _extraction = extraction;
    }

    public Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
        => job.JobType switch
        {
            "durable_transcription" => _transcription.ProcessAsync(job, cancellationToken),
            "durable_response" => _response.ProcessAsync(job, cancellationToken),
            "durable_extraction" when _extraction is not null => _extraction.ProcessAsync(job, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}")
        };
}
