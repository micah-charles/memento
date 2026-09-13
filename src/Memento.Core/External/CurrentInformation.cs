using Memento.Core.Domain;

namespace Memento.Core.External;

public interface ISearchProvider
{
    string Provider { get; }
    Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default);
}

public sealed class DeterministicSearchProvider : ISearchProvider
{
    public string Provider => "deterministic-test";

    public Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
        return Task.FromResult(new ExternalInformationResult(query, Provider, DateTimeOffset.UtcNow, [new ExternalInformationSource("Synthetic result", "https://example.invalid/result", "Synthetic external information for contract tests.")], true));
    }
}

public sealed class CurrentInformationService
{
    private readonly ISearchProvider _provider;

    public CurrentInformationService(ISearchProvider provider) => _provider = provider;

    public Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
        => _provider.SearchAsync(query, cancellationToken);
}
