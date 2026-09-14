using Memento.Core.Conversation;

namespace Memento.Core.Tests;

public sealed class ParticipantConversationStateTests
{
    [Fact]
    public void Normal_voice_loop_returns_to_listening()
    {
        var machine = new ParticipantConversationStateMachine();

        Assert.True(machine.TryTransition(ParticipantConversationState.Greeting));
        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.True(machine.TryTransition(ParticipantConversationState.Thinking));
        Assert.True(machine.TryTransition(ParticipantConversationState.Speaking));
        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.Equal(ParticipantConversationState.Listening, machine.Current);
    }

    [Fact]
    public void Participant_can_end_from_listening_or_speaking()
    {
        var machine = new ParticipantConversationStateMachine();

        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.True(machine.TryTransition(ParticipantConversationState.ConversationEnded));
        Assert.True(machine.TryTransition(ParticipantConversationState.Idle));
        Assert.True(machine.TryTransition(ParticipantConversationState.Greeting));
        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.True(machine.TryTransition(ParticipantConversationState.Thinking));
        Assert.True(machine.TryTransition(ParticipantConversationState.Speaking));
        Assert.True(machine.TryTransition(ParticipantConversationState.ConversationEnded));
    }

    [Fact]
    public void Recoverable_provider_failure_does_not_skip_to_speaking()
    {
        var machine = new ParticipantConversationStateMachine();

        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.True(machine.TryTransition(ParticipantConversationState.ErrorRecoverable));
        Assert.False(machine.TryTransition(ParticipantConversationState.Speaking));
        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
    }

    [Fact]
    public void Clarification_can_return_to_listening_without_overwriting_history()
    {
        var machine = new ParticipantConversationStateMachine();

        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.True(machine.TryTransition(ParticipantConversationState.Thinking));
        Assert.True(machine.TryTransition(ParticipantConversationState.Clarifying));
        Assert.True(machine.TryTransition(ParticipantConversationState.Listening));
        Assert.Equal(ParticipantConversationState.Listening, machine.Current);
    }

    [Fact]
    public void Invalid_transition_is_rejected()
    {
        var machine = new ParticipantConversationStateMachine();

        Assert.False(machine.TryTransition(ParticipantConversationState.Speaking));
        Assert.Equal(ParticipantConversationState.Idle, machine.Current);
    }
}
