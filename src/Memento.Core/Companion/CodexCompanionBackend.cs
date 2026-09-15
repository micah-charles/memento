using System.Text;
using System.Text.Json;

namespace Memento.Core.Companion;

public sealed class CodexCompanionBackend(ICodexRpc rpc, string workingDirectory) : ICompanionBackend
{
    public const string Instructions = "你係 MEMENTO，一位溫暖、有耐性嘅廣東話傾偈伙伴。用自然廣東話及繁體字，回答簡短，每次最多問一條問題。容許沉默，唔好迫人回憶，唔肯定就講唔肯定。唔好假裝記得未提供嘅事情。你只需要傾偈，沒有工具或檔案存取。";
    private bool _initialized;
    private string? _threadId;
    private string? _turnId;
    private readonly SemaphoreSlim _turnGate = new(1);
    private readonly Dictionary<string, object?> _config = new();

    public async Task<CompanionCapabilities> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            await rpc.CallAsync("initialize", new { clientInfo = new { name = "memento", title = "MEMENTO", version = "0.2.0" }, capabilities = new { experimentalApi = true } }, cancellationToken);
            _initialized = true;
            var effective = await rpc.CallAsync("config/read", new { includeLayers = false, cwd = workingDirectory }, cancellationToken);
            if (effective.TryGetProperty("config", out var config))
            {
                foreach (var section in new[] { "mcp_servers", "plugins" })
                    if (config.TryGetProperty(section, out var entries) && entries.ValueKind == JsonValueKind.Object)
                        foreach (var entry in entries.EnumerateObject()) _config[section + "." + JsonSerializer.Serialize(entry.Name) + ".enabled"] = false;
            }
            foreach (var feature in CodexRpc.DisabledFeatures) _config["features." + feature] = false;
            _config["features.skip_host_skill_discovery"] = true;
            _config["web_search"] = "disabled";
            _config["tools.update_plan.enabled"] = false;
        }
        var account = await rpc.CallAsync("account/read", new { refreshToken = false }, cancellationToken);
        var loggedIn = account.TryGetProperty("account", out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var type) && type.GetString() == "chatgpt";
        var models = new List<CompanionModel>();
        string? cursor = null;
        do
        {
            var page = await rpc.CallAsync("model/list", new { limit = 100, cursor, includeHidden = false }, cancellationToken);
            foreach (var model in page.GetProperty("data").EnumerateArray())
                models.Add(new(model.GetProperty("id").GetString()!, model.GetProperty("supportedReasoningEfforts").EnumerateArray().Select(e => e.GetProperty("reasoningEffort").GetString()!).ToArray()));
            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (cursor is not null);
        return new(loggedIn, models, loggedIn ? "已登入 Codex／ChatGPT" : "請先用 Codex 登入 ChatGPT 帳戶；不會改用 API key。");
    }

    public async Task<CompanionThread> BeginAsync(string model, string? resumeId = null, CancellationToken cancellationToken = default)
    {
        var capabilities = await ProbeAsync(cancellationToken);
        if (!capabilities.LoggedIn) throw new InvalidOperationException(capabilities.Status);
        if (!capabilities.Models.Any(m => m.Id == model && m.Efforts.Contains("low"))) throw new InvalidOperationException("請在家庭設定選擇可用且支援 low reasoning 嘅模型。");
        var parameters = new Dictionary<string, object?> { ["model"] = model, ["cwd"] = workingDirectory, ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["baseInstructions"] = Instructions, ["developerInstructions"] = Instructions, ["config"] = _config, ["personality"] = "none" };
        if (resumeId is not null) parameters["threadId"] = resumeId;
        var result = await rpc.CallAsync(resumeId is null ? "thread/start" : "thread/resume", parameters, cancellationToken);
        if (result.GetProperty("sandbox").GetProperty("type").GetString() != "readOnly" || result.GetProperty("model").GetString() != model)
            throw new InvalidOperationException("Codex did not honor companion isolation/model settings.");
        _threadId = result.GetProperty("thread").GetProperty("id").GetString()!;
        return new(_threadId, model);
    }

    public async Task<CompanionReply> SendAsync(CompanionThread thread, string text, Action<string>? onText = null, CancellationToken cancellationToken = default)
    {
        await _turnGate.WaitAsync(cancellationToken);
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new StringBuilder();
        void Receive(string method, JsonElement data)
        {
            if (method == "transport/closed") { completed.TrySetException(new IOException("Codex connection closed.")); return; }
            if (!data.TryGetProperty("threadId", out var id) || id.GetString() != thread.Id) return;
            if (method == "turn/started") _turnId = data.GetProperty("turn").GetProperty("id").GetString();
            if (method == "item/agentMessage/delta") { buffer.Append(data.GetProperty("delta").GetString()); onText?.Invoke(buffer.ToString()); }
            if (method == "item/completed" && data.TryGetProperty("item", out var item) && item.GetProperty("type").GetString() == "agentMessage")
            { var full = item.GetProperty("text").GetString(); if (buffer.Length == 0 && full is not null) buffer.Append(full); }
            if (method == "turn/completed") completed.TrySetResult(data.GetProperty("turn").Clone());
        }
        rpc.Notification += Receive;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            _threadId = thread.Id;
            var started = await rpc.CallAsync("turn/start", new { threadId = thread.Id, model = thread.Model, effort = "low", input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } }, clientUserMessageId = requestId }, timeout.Token);
            _turnId = started.GetProperty("turn").GetProperty("id").GetString();
            var ended = await completed.Task.WaitAsync(timeout.Token);
            if (ended.GetProperty("status").GetString() != "completed") throw new InvalidOperationException("Codex 未完成回覆；請檢查登入、網絡或使用限額。已保存原文。");
            if (buffer.Length == 0) throw new InvalidOperationException("Codex returned no conversational answer.");
            return new(thread.Id, _turnId!, requestId, thread.Model, buffer.ToString());
        }
        catch { try { await CancelAsync(); } catch { } throw; }
        finally { rpc.Notification -= Receive; _turnId = null; _turnGate.Release(); }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_threadId is not null && _turnId is not null) await rpc.CallAsync("turn/interrupt", new { threadId = _threadId, turnId = _turnId }, cancellationToken);
    }
    public ValueTask DisposeAsync() => rpc.DisposeAsync();
}
