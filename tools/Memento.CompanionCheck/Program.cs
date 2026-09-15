using Memento.Core.Companion;

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
