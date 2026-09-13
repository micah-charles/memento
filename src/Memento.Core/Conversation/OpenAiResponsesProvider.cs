using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Memento.Core.Conversation;

/// <summary>Optional text-response adapter used after local audio has been transcribed.</summary>
public sealed class OpenAiResponsesProvider : IConversationProvider
{
    private readonly HttpClient _httpClient;
    private readonly IApiCredentialProvider _credentials;

    public OpenAiResponsesProvider(HttpClient httpClient, IApiCredentialProvider credentials, string model)
    {
        _httpClient = httpClient;
        _credentials = credentials;
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
    }

    public string Provider => "openai";
    public string Model { get; }

    public async Task<ConversationResponse> SendAsync(ConversationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TranscriptText)) throw new InvalidOperationException("A transcript is required before a text response request.");
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");
        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            store = false,
            input = new[] { new { role = "user", content = new[] { new { type = "input_text", text = request.TranscriptText } } } }
        });
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.openai.com/v1/responses"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode) throw new ProviderRequestException($"OpenAI response failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = ExtractOutputText(document.RootElement);
        if (string.IsNullOrWhiteSpace(text)) throw new ProviderRequestException("OpenAI response did not contain output text.", (int)response.StatusCode);
        return new ConversationResponse(Provider, "conversation", Model, null, requestId, text, null, null, DateTimeOffset.UtcNow);
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String)
            return direct.GetString();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;
        var texts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())) texts.Add(text.GetString()!);
            }
        }
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }
}
