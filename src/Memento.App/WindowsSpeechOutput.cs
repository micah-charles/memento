using Memento.Core.Conversation;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Memento.App;

public sealed record LocalVoice(string Id, string Name, string Language)
{
    public override string ToString() => $"{Name} ({Language})";
}

public sealed class WindowsSpeechOutput(string voiceId) : ISpeechOutputProvider
{
    public string Provider => "windows-local";
    public string Model => "SpeechSynthesizer";
    public static IReadOnlyList<LocalVoice> Voices() => SpeechSynthesizer.AllVoices.Select(v => new LocalVoice(v.Id, v.DisplayName, v.Language)).ToArray();
    public async Task<SpeechOutputResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId) ?? throw new InvalidOperationException("請先選擇已安裝嘅語音。");
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);
        using var reader = new DataReader(stream);
        await reader.LoadAsync(checked((uint)stream.Size)).AsTask(cancellationToken);
        var bytes = new byte[checked((int)stream.Size)]; reader.ReadBytes(bytes);
        return new(Provider, Model, synthesizer.Voice.DisplayName, "wav", null, bytes, DateTimeOffset.UtcNow);
    }
}
