using System.Text.Json;

namespace Memento.Core.Companion;

public sealed record CompanionModel(string Id, IReadOnlyList<string> Efforts);
public sealed record CompanionCapabilities(bool LoggedIn, IReadOnlyList<CompanionModel> Models, string Status);
public sealed record CompanionThread(string Id, string Model);
public sealed record CompanionReply(string ThreadId, string TurnId, string RequestId, string Model, string Text);

public interface ICompanionBackend : IAsyncDisposable
{
    Task<CompanionCapabilities> ProbeAsync(CancellationToken cancellationToken = default);
    Task<CompanionThread> BeginAsync(string model, string? resumeId = null, CancellationToken cancellationToken = default);
    Task<CompanionReply> SendAsync(CompanionThread thread, string text, Action<string>? onText = null, CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface ICodexRpc : IAsyncDisposable
{
    event Action<string, JsonElement>? Notification;
    Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken = default);
}
