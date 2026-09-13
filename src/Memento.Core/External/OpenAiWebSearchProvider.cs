using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Memento.Core.Conversation;
using Memento.Core.Domain;

namespace Memento.Core.External;

/// <summary>Uses the Responses web-search tool while keeping external results untrusted and time-bound.</summary>
public sealed class OpenAiWebSearchProvider : ISearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly IApiCredentialProvider _credentials;
    private readonly string[] _allowedDomains;

    public OpenAiWebSearchProvider(HttpClient httpClient, IApiCredentialProvider credentials, string model, IEnumerable<string> allowedDomains)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
        _allowedDomains = NormalizeAllowedDomains(allowedDomains);
        if (_allowedDomains.Length == 0) throw new ArgumentException("At least one allowed search domain is required.", nameof(allowedDomains));
    }

    public string Provider => "openai-web-search";
    public string Model { get; }

    public async Task<ExternalInformationResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");

        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            store = false,
            input = query,
            tools = new[] { new { type = "web_search", filters = new { allowed_domains = _allowedDomains } } },
            include = new[] { "web_search_call.action.sources" }
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.openai.com/v1/responses"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
            throw new ProviderRequestException($"OpenAI web search failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = ParseResponse(document.RootElement, query);
        if (parsed.Sources.Count == 0)
            throw new ProviderRequestException("OpenAI web search returned no allowed sources.", 502);

        return parsed with { Provider = Provider };
    }

    private ExternalInformationResult ParseResponse(JsonElement root, string query)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new ExternalInformationResult(query, Provider, DateTimeOffset.UtcNow, [], true);
        var summary = root.TryGetProperty("output_text", out var directText) && directText.ValueKind == JsonValueKind.String
            ? directText.GetString() ?? string.Empty
            : string.Empty;
        var sources = new Dictionary<string, ExternalInformationSource>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object && action.TryGetProperty("sources", out var actionSources) && actionSources.ValueKind == JsonValueKind.Array)
                    AddSources(actionSources, sources, summary);
                if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object) continue;
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(summary))
                            summary = text.GetString() ?? string.Empty;
                        if (part.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var annotation in annotations.EnumerateArray())
                            {
                                if (!annotation.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) continue;
                                var title = annotation.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                                    ? titleElement.GetString()
                                    : null;
                                AddSource(url.GetString(), title, sources, summary);
                            }
                        }
                    }
                }
            }
        }

        return new ExternalInformationResult(query, Provider, DateTimeOffset.UtcNow, sources.Values.ToArray(), true);
    }

    private void AddSources(JsonElement sourceArray, IDictionary<string, ExternalInformationSource> sources, string snippet)
    {
        foreach (var source in sourceArray.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object) continue;
            var url = source.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
            var title = source.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null;
            AddSource(url, title, sources, snippet);
        }
    }

    private void AddSource(string? urlText, string? title, IDictionary<string, ExternalInformationSource> sources, string snippet)
    {
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsAllowed(uri.Host)) return;
        var sourceTitle = string.IsNullOrWhiteSpace(title) ? uri.Host : title.Trim();
        sources[uri.AbsoluteUri] = new ExternalInformationSource(sourceTitle, uri.AbsoluteUri, snippet.Trim());
    }

    private bool IsAllowed(string host)
        => _allowedDomains.Any(domain => string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static string[] NormalizeAllowedDomains(IEnumerable<string> domains)
        => domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim().Trim('.').ToLowerInvariant())
            .Where(domain => Uri.CheckHostName(domain) is UriHostNameType.Dns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
