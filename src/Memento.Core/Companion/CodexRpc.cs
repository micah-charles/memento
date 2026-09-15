using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Memento.Core.Companion;

/// <summary>Owned stdio child. Protocol and stderr never go into application logs.</summary>
public sealed class CodexRpc : ICodexRpc
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private readonly Task _errors;
    private long _nextId;
    public event Action<string, JsonElement>? Notification;

    public static readonly string[] DisabledFeatures = ["shell_tool", "unified_exec", "apply_patch_freeform", "code_mode", "js_repl", "apps", "connectors", "computer_use", "browser_use", "in_app_browser", "multi_agent", "collab", "hooks", "codex_hooks", "memory_tool", "memories", "image_generation", "view_image", "tool_search", "skill_search", "goals", "undo"];

    public CodexRpc(string executable, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        foreach (var feature in DisabledFeatures) AddOverride(start, "features." + feature + "=false");
        AddOverride(start, "features.skip_host_skill_discovery=true");
        AddOverride(start, "web_search=\"disabled\"");
        AddOverride(start, "tools.update_plan.enabled=false");
        AddOverride(start, "sandbox_mode=\"read-only\"");
        AddOverride(start, "approval_policy=\"never\"");
        _process = Process.Start(start) ?? throw new InvalidOperationException("Codex could not start.");
        _reader = ReadAsync();
        _errors = DrainErrorsAsync();
    }

    private static void AddOverride(ProcessStartInfo start, string value) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(value); }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, timeout.Token);
            var result = await completion.Task.WaitAsync(timeout.Token);
            if (method == "initialize") await WriteAsync(new { method = "initialized", @params = new { } }, timeout.Token);
            return result;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object value, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token); await _process.StandardInput.FlushAsync(token); }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("method", out var method))
                {
                    if (root.TryGetProperty("id", out var requestId))
                    {
                        // Never approve tools, file changes, permissions or arbitrary server requests.
                        await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Companion tools are disabled" } }, _stop.Token);
                    }
                    else Notification?.Invoke(method.GetString()!, root.GetProperty("params").Clone());
                }
                else if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var waiting))
                {
                    if (root.TryGetProperty("error", out _)) waiting.TrySetException(new InvalidOperationException("Codex protocol request failed: check login, model and version."));
                    else waiting.TrySetResult(root.GetProperty("result").Clone());
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            foreach (var completion in _pending.Values) completion.TrySetException(new IOException("Codex connection closed."));
            Notification?.Invoke("transport/closed", JsonSerializer.SerializeToElement(new { }));
        }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (await _process.StandardError.ReadLineAsync(_stop.Token) is not null) { } }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _process.StandardInput.Close(); if (!_process.HasExited) { using var timeout = new CancellationTokenSource(2000); await _process.WaitForExitAsync(timeout.Token); } }
        catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        await Task.WhenAll(_reader, _errors);
        _process.Dispose();
    }

    public static string? FindExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MEMENTO_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath) && Path.GetExtension(explicitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(explicitPath);
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var direct = Path.Combine(path, "codex.exe");
            if (File.Exists(direct)) return direct;
            var npm = Path.Combine(path, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            if (File.Exists(npm)) return npm;
        }
        return null;
    }
}
