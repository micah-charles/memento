namespace Memento.Core.Conversation;

/// <summary>
/// The small set of states exposed by the participant conversation surface.
/// Storage, provider, and admin workflows remain separate from this state.
/// </summary>
public enum ParticipantConversationState
{
    Idle,
    Greeting,
    Listening,
    Thinking,
    Speaking,
    Clarifying,
    Offline,
    ErrorRecoverable,
    ConversationEnded
}

/// <summary>
/// Validates participant-facing conversation transitions without depending on WinUI.
/// A failed provider or unavailable device may move the shell to a recoverable state;
/// it never makes a local Source invalid.
/// </summary>
public sealed class ParticipantConversationStateMachine
{
    public ParticipantConversationState Current { get; private set; } = ParticipantConversationState.Idle;

    public bool TryTransition(ParticipantConversationState next)
    {
        if (Current == next) return true;
        if (!IsAllowed(Current, next)) return false;
        Current = next;
        return true;
    }

    public void Reset() => Current = ParticipantConversationState.Idle;

    private static bool IsAllowed(ParticipantConversationState from, ParticipantConversationState to)
        => from switch
        {
            ParticipantConversationState.Idle => to is ParticipantConversationState.Greeting or ParticipantConversationState.Listening or ParticipantConversationState.ConversationEnded,
            ParticipantConversationState.Greeting => to is ParticipantConversationState.Listening or ParticipantConversationState.Thinking or ParticipantConversationState.ConversationEnded or ParticipantConversationState.Offline or ParticipantConversationState.ErrorRecoverable,
            ParticipantConversationState.Listening => to is ParticipantConversationState.Thinking or ParticipantConversationState.Clarifying or ParticipantConversationState.ConversationEnded or ParticipantConversationState.Offline or ParticipantConversationState.ErrorRecoverable,
            ParticipantConversationState.Thinking => to is ParticipantConversationState.Speaking or ParticipantConversationState.Clarifying or ParticipantConversationState.Offline or ParticipantConversationState.ErrorRecoverable or ParticipantConversationState.ConversationEnded,
            ParticipantConversationState.Speaking => to is ParticipantConversationState.Listening or ParticipantConversationState.Clarifying or ParticipantConversationState.ConversationEnded or ParticipantConversationState.Offline or ParticipantConversationState.ErrorRecoverable,
            ParticipantConversationState.Clarifying => to is ParticipantConversationState.Listening or ParticipantConversationState.Thinking or ParticipantConversationState.ConversationEnded or ParticipantConversationState.ErrorRecoverable,
            ParticipantConversationState.Offline => to is ParticipantConversationState.Listening or ParticipantConversationState.ConversationEnded or ParticipantConversationState.ErrorRecoverable,
            ParticipantConversationState.ErrorRecoverable => to is ParticipantConversationState.Listening or ParticipantConversationState.Offline or ParticipantConversationState.ConversationEnded,
            ParticipantConversationState.ConversationEnded => to is ParticipantConversationState.Idle or ParticipantConversationState.Greeting or ParticipantConversationState.Listening,
            _ => false
        };
}
