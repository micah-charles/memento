using Memento.Core.Companion;
using NAudio.Wave;
using System.Text.Json;

if (args.Contains("--voices"))
{
    foreach (var voice in Memento.App.WindowsSpeechOutput.Voices()) Console.WriteLine($"{voice.Name}: {voice.Language}");
    return;
}

if (args.Contains("--tts"))
{
    var voice = Memento.App.WindowsSpeechOutput.Voices().FirstOrDefault(v => v.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        ?? Memento.App.WindowsSpeechOutput.Voices().FirstOrDefault(v => v.Language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("No Mandarin-compatible Windows voice is installed.");
    var output = await new Memento.App.WindowsSpeechOutput(voice.Id).SynthesizeAsync("你好，呢個係 MEMENTO 本機語音測試。", CancellationToken.None);
    var path = Path.Combine(Path.GetTempPath(), "memento-local-tts-" + Guid.NewGuid().ToString("N") + ".wav");
    await File.WriteAllBytesAsync(path, output.AudioBytes);
    Console.WriteLine($"Local TTS: {voice.Name} ({voice.Language}); WAV: {path}; bytes: {output.AudioBytes.Length}");
    return;
}

if (args.Contains("--stt-synthetic"))
{
    var voice = Memento.App.WindowsSpeechOutput.Voices().FirstOrDefault(v => v.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
        ?? Memento.App.WindowsSpeechOutput.Voices().FirstOrDefault(v => v.Language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("No Mandarin-compatible Windows voice is installed.");
    var speech = await new Memento.App.WindowsSpeechOutput(voice.Id).SynthesizeAsync("你好，呢個係 MEMENTO 本機辨識測試。", CancellationToken.None);
    using var wav = new WaveFileReader(new MemoryStream(speech.AudioBytes));
    if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16)
        throw new InvalidDataException($"Unexpected local voice format: {wav.WaveFormat}");
    var pcm16 = new byte[wav.Length];
    _ = wav.Read(pcm16, 0, pcm16.Length);
    var pcm48 = new byte[checked(pcm16.Length * 3)];
    for (var i = 0; i < pcm16.Length; i += 2)
        for (var repeat = 0; repeat < 3; repeat++) { pcm48[i * 3 + repeat * 2] = pcm16[i]; pcm48[i * 3 + repeat * 2 + 1] = pcm16[i + 1]; }
    var speechRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MEMENTO", "speech");
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(speechRoot, "local-speech.json")));
    var transcript = await new WhisperLocalTranscription(manifest.RootElement.GetProperty("executable").GetString()!, manifest.RootElement.GetProperty("model").GetString()!, Path.Combine(speechRoot, "scratch")).TranscribeAsync(pcm48);
    Console.WriteLine($"Synthetic local STT ({voice.Name}, {voice.Language}): {transcript.Text}");
    Console.WriteLine($"Segments: {transcript.Segments.Count}; input PCM48 bytes: {pcm48.Length}");
    return;
}

var executable = CodexRpc.FindExecutable() ?? throw new InvalidOperationException("Set MEMENTO_CODEX_PATH to codex.exe.");
var cwd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MEMENTO", "companion-workspace");
await using var backend = new CodexCompanionBackend(new CodexRpc(executable, cwd), cwd);
var capabilities = await backend.ProbeAsync();
Console.WriteLine($"ChatGPT login: {capabilities.LoggedIn}; models: {string.Join(",", capabilities.Models.Select(m => m.Id))}");
if (args.Contains("--live"))
{
    var thread = await backend.BeginAsync("gpt-5.6-luna");
    foreach (var prompt in new[] { "測試對話：我嘅暗號係紫色茶杯。請簡短確認。", "我今日想飲茶。你想問我咩？", "頭先我話暗號係咩？只答暗號。" })
    {
        var reply = await backend.SendAsync(thread, prompt);
        Console.WriteLine($"Turn {reply.TurnId}: {reply.Text}");
    }
}
