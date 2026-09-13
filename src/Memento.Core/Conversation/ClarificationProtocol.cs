using Memento.Core.Domain;
using Memento.Core.Storage;

namespace Memento.Core.Conversation;

public enum ClarificationEntityKind
{
    None,
    PersonName,
    Place,
    Relationship,
    Date,
    Year,
    MajorEvent,
    Identity,
    Preference
}

public sealed record ClarificationCandidate(
    string RecognizedText,
    ClarificationEntityKind EntityKind,
    double? Confidence,
    bool Ambiguous,
    bool HasFutureMemoryImpact);

public sealed record ClarificationDecision(bool ShouldAsk, string Reason);

public static class ClarificationPolicy
{
    public static ClarificationDecision Decide(ClarificationCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.RecognizedText))
            return new ClarificationDecision(false, "There is no candidate wording to clarify.");
        var highValue = candidate.EntityKind is ClarificationEntityKind.PersonName or ClarificationEntityKind.Place or ClarificationEntityKind.Relationship or ClarificationEntityKind.Date or ClarificationEntityKind.Year or ClarificationEntityKind.Identity or ClarificationEntityKind.MajorEvent or ClarificationEntityKind.Preference;
        if (highValue && (candidate.Ambiguous || candidate.HasFutureMemoryImpact || candidate.Confidence is null or < 0.9d))
            return new ClarificationDecision(true, "High-value or ambiguous wording may affect future memory.");
        if (candidate.Confidence is < 0.55d)
            return new ClarificationDecision(true, "Very low confidence warrants a targeted check.");
        return new ClarificationDecision(false, "The wording is low-impact or sufficiently clear.");
    }
}

public sealed record ClarificationChain(
    TranscriptRevision InitialRevision,
    ClarificationEvent Event,
    TranscriptRevision? CorrectedRevision,
    VocabularyEntry? Vocabulary);

public sealed class ClarificationProtocol
{
    private readonly ArchiveRepository _repository;

    public ClarificationProtocol(ArchiveRepository repository) => _repository = repository;

    public TranscriptRevision AddInitialRevision(string sourceId, string? turnId, string recognizedText, double? confidence, DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(recognizedText)) throw new ArgumentException("Initial recognition is required.", nameof(recognizedText));
        return _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), sourceId, turnId, 1, "initial", recognizedText, confidence, null, createdAt ?? DateTimeOffset.UtcNow));
    }

    public ClarificationChain RecordOutcome(
        Session session,
        TranscriptRevision initialRevision,
        ClarificationEntityKind entityKind,
        string questionText,
        string? participantResponseText,
        ClarificationOutcome outcome,
        string? correctedText = null,
        string? canonicalText = null,
        string? context = null,
        DateTimeOffset? occurredAt = null)
    {
        if (string.IsNullOrWhiteSpace(questionText)) throw new ArgumentException("A clarification question is required.", nameof(questionText));
        if (outcome is ClarificationOutcome.SpeakerConfirmed or ClarificationOutcome.CorrectedPreviousCorrection && string.IsNullOrWhiteSpace(correctedText))
            throw new ArgumentException("A speaker-confirmed outcome requires corrected wording.", nameof(correctedText));

        var source = _repository.GetSource(initialRevision.SourceId)
            ?? throw new InvalidOperationException("The clarification Source was not found.");
        if (!string.Equals(source.SessionId, session.SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("The clarification Source does not belong to the supplied session.");
        if (initialRevision.TurnId is not null && !string.Equals(initialRevision.TurnId, source.TurnId, StringComparison.Ordinal))
            throw new InvalidOperationException("The clarification transcript turn does not match the Source turn.");
        var persistedInitial = _repository.ListTranscriptRevisions(initialRevision.SourceId)
            .FirstOrDefault(revision => string.Equals(revision.TranscriptRevisionId, initialRevision.TranscriptRevisionId, StringComparison.Ordinal));
        if (persistedInitial is null)
            throw new InvalidOperationException("The initial transcript revision was not found for the clarification Source.");

        var now = occurredAt ?? DateTimeOffset.UtcNow;
        TranscriptRevision? corrected = null;
        if (!string.IsNullOrWhiteSpace(correctedText))
        {
            corrected = _repository.AddTranscriptRevision(new TranscriptRevision(Guid.NewGuid().ToString("N"), initialRevision.SourceId, initialRevision.TurnId, initialRevision.RevisionNumber + 1, "corrected", correctedText, 1d, initialRevision.TranscriptRevisionId, now));
        }

        var clarification = _repository.AddClarificationEvent(new ClarificationEvent(Guid.NewGuid().ToString("N"), session.SessionId, initialRevision.TurnId, initialRevision.SourceId, entityKind.ToString(), questionText, initialRevision.TranscriptRevisionId, participantResponseText, corrected?.TranscriptRevisionId, outcome, now, DateTimeOffset.UtcNow));
        VocabularyEntry? vocabulary = null;
        if (corrected is not null && outcome is ClarificationOutcome.SpeakerConfirmed or ClarificationOutcome.CorrectedPreviousCorrection)
        {
            var canonical = string.IsNullOrWhiteSpace(canonicalText) ? corrected.Text : canonicalText;
            vocabulary = _repository.AddVocabularyEntry(new VocabularyEntry(Guid.NewGuid().ToString("N"), canonical, initialRevision.Text, context, true, clarification.ClarificationEventId, DateTimeOffset.UtcNow));
        }

        // A corrected transcript is a new extraction input. Queue it only when the
        // session has already granted cloud processing; the worker re-checks this
        // policy before any provider call.
        if (corrected is not null
            && session.PrivacyMode != PrivacyMode.LocalCaptureOnly
            && _repository.HasGrantedConsent(session.SessionId, ConsentScope.CloudTranscription))
        {
            new ConversationSessionWriter(_repository).QueueExtractionIfNeeded(session.SessionId, initialRevision.TurnId, initialRevision.SourceId, corrected.TranscriptRevisionId, now);
        }

        return new ClarificationChain(initialRevision, clarification, corrected, vocabulary);
    }
}
