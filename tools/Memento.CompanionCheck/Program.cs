using Memento.Core.Companion;

if (args.Contains("--voices"))
{
    foreach (var voice in Memento.App.WindowsSpeechOutput.Voices()) Console.WriteLine($"{voice.Name}: {voice.Language}");
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
