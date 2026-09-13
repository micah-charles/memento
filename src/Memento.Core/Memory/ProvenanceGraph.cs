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
        if (link.EvidenceId != evidence.EvidenceId || link.MemoryClaimId != claim.MemoryClaimId || !Relationships.Contains(link.Relationship, StringComparer.Ordinal))
            throw new InvalidOperationException("Evidence-to-claim link is not a valid provenance relationship.");
        if (claim.Status == ClaimStatus.Reviewed && evidence.Kind == EvidenceKind.AiInference && !evidence.SpeakerConfirmed)
            throw new InvalidOperationException("An unconfirmed AI inference cannot silently become a reviewed claim.");
    }
}
