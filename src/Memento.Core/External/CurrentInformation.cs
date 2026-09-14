using Memento.Core.Domain;
using Memento.Core.Conversation;

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

    public async Task<ExternalInformationResult> SearchAsync(
        string query,
        PrivacyMode privacyMode,
        bool cloudConsent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A search query is required.", nameof(query));
        if (CloudNotPermittedException.IsBlocked(privacyMode))
            throw new CloudNotPermittedException();
        if (!cloudConsent)
            throw new CloudNotPermittedException("Cloud consent is required for current-information queries.");
        var result = await _provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        // External providers never get authority to label their output as
        // trusted or to spoof the query/provider identity used for audit and
        // display. Current-information answers remain time-bound external data.
        return result with
        {
            Query = query,
            Provider = _provider.Provider,
            IsUntrustedExternalInformation = true
        };
    }

    /// <summary>
    /// Routes only an explicit present-time question from a participant turn.
    /// A null result means the turn remains on the normal conversation path.
    /// </summary>
    public async Task<ExternalInformationResult?> TrySearchFromTranscriptAsync(
        string? transcript,
        PrivacyMode privacyMode,
        bool cloudConsent,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentInformationIntentDetector.TryDetect(transcript, out var match)) return null;
        return await SearchAsync(match.Query, privacyMode, cloudConsent, cancellationToken).ConfigureAwait(false);
    }
}
