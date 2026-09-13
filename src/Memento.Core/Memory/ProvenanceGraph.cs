using Memento.Core.Domain;

namespace Memento.Core.Memory;

public static class ProvenanceGraph
{
    private static readonly string[] Relationships = ["supports", "weakens", "contradicts", "clarifies", "contextualises"];

    public static void Validate(SourceMetadata source, TranscriptRevision revision, EvidenceRecord evidence, MemoryClaim claim, EvidenceClaimLink link)
    {
        if (revision.SourceId != source.SourceId || evidence.SourceId != source.SourceId)
            throw new InvalidOperationException("Source provenance is inconsistent.");
        if (evidence.TranscriptRevisionId != revision.TranscriptRevisionId)
            throw new InvalidOperationException("Evidence must point to the transcript revision that produced it.");
        if (evidence.AudioStartMs is < 0 || evidence.AudioEndMs is < 0 || evidence.AudioStartMs is not null && evidence.AudioEndMs is not null && evidence.AudioStartMs > evidence.AudioEndMs)
            throw new InvalidOperationException("Evidence audio span is invalid.");
        if (evidence.Kind == EvidenceKind.AiInference && (string.IsNullOrWhiteSpace(evidence.ExtractionProvider) || string.IsNullOrWhiteSpace(evidence.ExtractionModel)))
            throw new InvalidOperationException("AI inference evidence requires extraction provider metadata.");
        if (link.EvidenceId != evidence.EvidenceId || link.MemoryClaimId != claim.MemoryClaimId || !Relationships.Contains(link.Relationship, StringComparer.Ordinal))
            throw new InvalidOperationException("Evidence-to-claim link is not a valid provenance relationship.");
        if (claim.Status == ClaimStatus.Reviewed && evidence.Kind == EvidenceKind.AiInference && !evidence.SpeakerConfirmed)
            throw new InvalidOperationException("An unconfirmed AI inference cannot silently become a reviewed claim.");
    }

    public static void ValidateResponseEpisode(ResponseEpisode episode, EvidenceRecord stimulus, EvidenceRecord response, EvidenceRecord? followUp = null)
    {
        if (episode.StimulusEvidenceId != stimulus.EvidenceId || episode.ResponseEvidenceId != response.EvidenceId)
            throw new InvalidOperationException("Response Episode evidence links are inconsistent.");
        if (episode.FollowUpEvidenceId is not null && (followUp is null || episode.FollowUpEvidenceId != followUp.EvidenceId))
            throw new InvalidOperationException("Response Episode follow-up evidence is inconsistent.");
        if (episode.SessionId != stimulus.SessionId || episode.SessionId != response.SessionId || followUp?.SessionId != episode.SessionId && followUp is not null)
            throw new InvalidOperationException("Response Episode session provenance is inconsistent.");
        if (string.IsNullOrWhiteSpace(episode.ObservedDetails) || string.IsNullOrWhiteSpace(episode.ObservationBasis))
            throw new InvalidOperationException("Response Episode observation details and basis are required.");
    }
}
