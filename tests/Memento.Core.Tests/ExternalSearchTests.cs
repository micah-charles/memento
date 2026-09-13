using System.Net;
using System.Net.Http.Headers;
using Memento.Core.Conversation;
using Memento.Core.External;

namespace Memento.Core.Tests;

public sealed class ExternalSearchTests
{
    [Fact]
    public async Task OpenAi_web_search_sends_allowlist_and_extracts_only_allowed_https_sources()
    {
        var handler = new SearchHandler("""
            {
              "output_text": "香港天氣回覆",
              "output": [
                { "type": "web_search_call", "action": { "sources": [
                  { "type": "url", "title": "Hong Kong Observatory", "url": "https://www.hko.gov.hk/en/weather-forecast/weather-forecast-for-hong-kong.html" },
                  { "type": "url", "title": "Untrusted", "url": "https://outside.example/news" }
                ] } }
              ]
            }
            """);
        using var http = new HttpClient(handler);
        var provider = new OpenAiWebSearchProvider(http, new FixedCredentialProvider(), "gpt-5.6-terra", ["hko.gov.hk"]);

        var result = await provider.SearchAsync("香港今日天氣");

        Assert.True(result.IsUntrustedExternalInformation);
        Assert.Equal("openai-web-search", result.Provider);
        Assert.Equal("香港天氣回覆", result.Sources.Single().Snippet);
        Assert.Equal("https://www.hko.gov.hk/en/weather-forecast/weather-forecast-for-hong-kong.html", result.Sources[0].Url);
        Assert.Contains("\"store\":false", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"allowed_domains\":[\"hko.gov.hk\"]", handler.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer test-key", handler.Authorization);
    }

    [Fact]
    public async Task OpenAi_web_search_rejects_a_response_without_allowed_sources()
    {
        var handler = new SearchHandler("""
            {
              "output_text": "結果",
              "output": [
                { "type": "web_search_call", "action": { "sources": [
                  { "type": "url", "url": "https://outside.example/news" },
                  { "type": "url", "url": "http://hko.gov.hk/insecure" }
                ] } }
              ]
            }
            """);
        using var http = new HttpClient(handler);
        var provider = new OpenAiWebSearchProvider(http, new FixedCredentialProvider(), "gpt-5.6-terra", ["hko.gov.hk"]);

        await Assert.ThrowsAsync<ProviderRequestException>(() => provider.SearchAsync("香港今日天氣"));
    }

    private sealed class FixedCredentialProvider : IApiCredentialProvider
    {
        public string? GetApiKey() => "test-key";
    }

    private sealed class SearchHandler(string responseBody) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string Authorization { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Headers = { { "x-request-id", "search-test" } },
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
