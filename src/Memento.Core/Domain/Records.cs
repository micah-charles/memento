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

public sealed record DerivedSpeechAsset(
    string DerivedSpeechAssetId,
    string SessionId,
    string? TurnId,
    string FilePath,
    string Format,
    long ByteLength,
    string Sha256,
    string Provider,
    string Model,
    string Voice,
    string? RequestId,
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

public enum ConversationJobStatus
{
    Pending,
    Processing,
    Succeeded,
    Failed
}

public sealed record ConversationJob(
    string ConversationJobId,
    string SessionId,
    string? TurnId,
    string SourceId,
    string JobType,
    ConversationJobStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TranscriptRevisionId = null);

public enum EvidenceKind
{
    DirectStatement,
    ConfirmedInterpretation,
    AiInference,
    SystemObservation,
    ExternalFact
}

public enum ParticipantCertainty
{
    Stated,
    Uncertain,
    Unknown,
    NotApplicable
}

public sealed record EvidenceRecord(
    string EvidenceId,
    EvidenceKind Kind,
    string SourceId,
    string? SessionId,
    string? TurnId,
    string? TranscriptRevisionId,
    string Statement,
    string OriginalExpression,
    ParticipantCertainty ParticipantCertainty,
    bool SpeakerConfirmed,
    DateTimeOffset CreatedAt,
    long? AudioStartMs = null,
    long? AudioEndMs = null,
    string? ExtractionProvider = null,
    string? ExtractionModel = null);

public enum ClaimStatus
{
    Candidate,
    Reviewed,
    Rejected
}

public sealed record MemoryClaim(
    string MemoryClaimId,
    string Statement,
    string? SubjectPersonId,
    string Predicate,
    string Object,
    ClaimStatus Status,
    DateTimeOffset CreatedAt);

public sealed record EvidenceClaimLink(
    string EvidenceId,
    string MemoryClaimId,
    string Relationship,
    DateTimeOffset CreatedAt);

public sealed record ClaimEvidence(EvidenceRecord Evidence, string Relationship);

public sealed record PersonEntity(
    string PersonEntityId,
    string DisplayName,
    string? Relationship,
    DateTimeOffset CreatedAt);

public sealed record EntityAlias(
    string EntityAliasId,
    string PersonEntityId,
    string Alias,
    bool SpeakerConfirmed,
    string? SourceClarificationEventId,
    DateTimeOffset CreatedAt);

public sealed record EvidenceEntityLink(
    string EvidenceId,
    string PersonEntityId,
    string Role,
    DateTimeOffset CreatedAt);

public sealed record ExternalInformationSource(string Title, string Url, string Snippet);

public sealed record ExternalInformationResult(
    string Query,
    string Provider,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<ExternalInformationSource> Sources,
    bool IsUntrustedExternalInformation);

public sealed record ReviewAnnotation(
    string AnnotationId,
    string TargetType,
    string TargetId,
    string ActorId,
    string AnnotationType,
    string Body,
    string? Assessment,
    DateTimeOffset CreatedAt);

public sealed record ResponseEpisode(
    string ResponseEpisodeId,
    string SessionId,
    string StimulusEvidenceId,
    string ResponseEvidenceId,
    string? FollowUpEvidenceId,
    string ObservedDetails,
    string ObservationBasis,
    DateTimeOffset CreatedAt);

public sealed record AppSetting(string Key, string Value, DateTimeOffset UpdatedAt);
