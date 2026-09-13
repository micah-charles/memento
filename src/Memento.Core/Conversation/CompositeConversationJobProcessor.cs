using Memento.Core.Domain;

namespace Memento.Core.Conversation;

/// <summary>Routes persisted conversation jobs to the capability-specific processor.</summary>
public sealed class CompositeConversationJobProcessor : IConversationJobProcessor
{
    private readonly IConversationJobProcessor _transcription;
    private readonly IConversationJobProcessor _response;

    public CompositeConversationJobProcessor(IConversationJobProcessor transcription, IConversationJobProcessor response)
    {
        _transcription = transcription;
        _response = response;
    }

    public Task ProcessAsync(ConversationJob job, CancellationToken cancellationToken = default)
        => job.JobType switch
        {
            "durable_transcription" => _transcription.ProcessAsync(job, cancellationToken),
            "durable_response" => _response.ProcessAsync(job, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported conversation job type: {job.JobType}")
        };
}
