namespace Memento.Core.Domain;

public enum PrivacyMode
{
    Normal,
    PrivateConversation,
    LocalCaptureOnly
}

public enum ConsentScope
{
    LocalCapture,
    CloudTranscription,
    LiveCloudConversation,
    FamilyAdminSharing
}

public sealed record Session(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    PrivacyMode PrivacyMode,
    DateTimeOffset CreatedAt);

public sealed record Turn(
    string TurnId,
    string SessionId,
    int SequenceNumber,
    string SpeakerType,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt);

public sealed record ConsentEvent(
    string ConsentEventId,
    string SessionId,
    string? PersonId,
    ConsentScope Scope,
    PrivacyMode PrivacyMode,
    bool Granted,
    string NoticeVersion,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record SourceMetadata(
    string SourceId,
    string SourceType,
    string? SessionId,
    string? TurnId,
    string? FilePath,
    string? Format,
    int? SampleRate,
    int? Channels,
    int? BitDepth,
    long? ByteLength,
    long? DurationMs,
    string? Sha256,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinalizedAt,
    string RecoveryStatus,
    DateTimeOffset CreatedAt);

public sealed record ProviderInteraction(
    string ProviderInteractionId,
    string SessionId,
    string? TurnId,
    string Provider,
    string Capability,
    string Model,
    string? ModelSnapshot,
    string? RequestId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long? InputAudioMs,
    long? OutputAudioMs,
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public enum ClarificationOutcome
{
    SpeakerConfirmed,
    ParticipantRefused,
    ParticipantDoesNotRemember,
    TwoPossibilities,
    CorrectedPreviousCorrection
}

public sealed record TranscriptRevision(
    string TranscriptRevisionId,
    string SourceId,
    string? TurnId,
    int RevisionNumber,
    string RevisionKind,
    string Text,
    double? Confidence,
    string? ParentRevisionId,
    DateTimeOffset CreatedAt);

public sealed record ClarificationEvent(
    string ClarificationEventId,
    string SessionId,
    string? TurnId,
    string SourceId,
    string TriggerKind,
    string QuestionText,
    string InitialRevisionId,
    string? ParticipantResponseText,
    string? CorrectedRevisionId,
    ClarificationOutcome Outcome,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record VocabularyEntry(
    string VocabularyEntryId,
    string CanonicalText,
    string PreviousRecognition,
    string? Context,
    bool SpeakerConfirmed,
    string SourceClarificationEventId,
    DateTimeOffset CreatedAt);
