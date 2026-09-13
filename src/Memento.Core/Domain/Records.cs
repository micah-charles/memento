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
