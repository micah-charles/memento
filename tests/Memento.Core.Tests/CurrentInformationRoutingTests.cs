using Memento.Core.Domain;
using Memento.Core.Conversation;
using Memento.Core.External;

namespace Memento.Core.Tests;

public sealed class CurrentInformationRoutingTests
{
    [Fact]
    public async Task Explicit_current_question_is_searched_with_consent()
    {
        var service = new CurrentInformationService(new DeterministicSearchProvider());

        var result = await service.TrySearchFromTranscriptAsync("今日香港落唔落雨呀？", PrivacyMode.Normal, cloudConsent: true);

        Assert.NotNull(result);
        Assert.Equal("今日香港落唔落雨呀？", result!.Query);
        Assert.True(result.IsUntrustedExternalInformation);
    }

    [Fact]
    public async Task Personal_statement_does_not_call_search()
    {
        var service = new CurrentInformationService(new ThrowingSearchProvider());

        var result = await service.TrySearchFromTranscriptAsync("我最怕落雨。", PrivacyMode.Normal, cloudConsent: true);

        Assert.Null(result);
    }

    [Fact]
    public async Task Routed_question_still_requires_consent_and_privacy_policy()
    {
        var service = new CurrentInformationService(new DeterministicSearchProvider());

        await Assert.ThrowsAsync<CloudNotPermittedException>(() => service.TrySearchFromTranscriptAsync("今日香港落唔落雨呀？", PrivacyMode.LocalCaptureOnly, cloudConsent: true));
        await Assert.ThrowsAsync<CloudNotPermittedException>(() => service.TrySearchFromTranscriptAsync("今日香港落唔落雨呀？", PrivacyMode.Normal, cloudConsent: false));
    }

    private sealed class ThrowingSearchProvider : ISearchProvider
    {
        public string Provider => "throwing";
        public Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Search should not be called for a personal statement.");
    }
}
