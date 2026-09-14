using System.Text.Json;
using Memento.Core.Audio;
using Memento.Core.Conversation;
using Memento.Core.Domain;

namespace Memento.Core.Tests;

public sealed class RealtimeTurnDetectionTests
{
    [Fact]
    public void Disabled_turn_detection_preserves_push_to_talk_contract()
    {
        var options = new RealtimeTurnDetectionOptions();

        Assert.Null(options.ToPayload());
    }

    [Fact]
    public void Semantic_vad_defaults_to_low_eagerness_for_longer_speech_pauses()
    {
        var options = new RealtimeTurnDetectionOptions(RealtimeTurnDetectionMode.SemanticVad);
        var payload = JsonSerializer.Serialize(options.ToPayload());

        Assert.Contains("semantic_vad", payload, StringComparison.Ordinal);
        Assert.Contains("\"eagerness\":\"low\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"interrupt_response\":true", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_vad_keeps_conservative_silence_window()
    {
        var options = new RealtimeTurnDetectionOptions(RealtimeTurnDetectionMode.ServerVad);
        var payload = JsonSerializer.Serialize(options.ToPayload());

        Assert.Contains("server_vad", payload, StringComparison.Ordinal);
        Assert.Contains("\"silence_duration_ms\":900", payload, StringComparison.Ordinal);
    }
}
