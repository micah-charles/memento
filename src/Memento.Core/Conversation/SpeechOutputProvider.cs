using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Memento.Core.Conversation;

public sealed record SpeechOutputResult(string Provider, string Model, string Voice, string Format, string? RequestId, byte[] AudioBytes, DateTimeOffset CompletedAt);

public interface ISpeechOutputProvider
{
    string Provider { get; }
    string Model { get; }
    Task<SpeechOutputResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class DeterministicSpeechOutputProvider : ISpeechOutputProvider
{
    public string Provider => "deterministic-test";
    public string Model => "fake-tts-v1";

    public Task<SpeechOutputResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Speech text is required.", nameof(text));
        return Task.FromResult(new SpeechOutputResult(Provider, Model, "test", "wav", "fake-speech", Encoding.UTF8.GetBytes("synthetic audio"), DateTimeOffset.UtcNow));
    }
}

/// <summary>Optional OpenAI speech output adapter. Generated audio is derived output, never participant evidence.</summary>
public sealed class OpenAiSpeechOutputProvider : ISpeechOutputProvider
{
    private readonly HttpClient _httpClient;
    private readonly IApiCredentialProvider _credentials;
    private readonly string _voice;

    public OpenAiSpeechOutputProvider(HttpClient httpClient, IApiCredentialProvider credentials, string model = "gpt-4o-mini-tts", string voice = "alloy")
    {
        _httpClient = httpClient;
        _credentials = credentials;
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
        _voice = string.IsNullOrWhiteSpace(voice) ? throw new ArgumentException("A voice is required.", nameof(voice)) : voice;
    }

    public string Provider => "openai";
    public string Model { get; }

    public async Task<SpeechOutputResult> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Speech text is required.", nameof(text));
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");
        var payload = JsonSerializer.Serialize(new { model = Model, voice = _voice, input = text, response_format = "wav" });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.openai.com/v1/audio/speech"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode) throw new ProviderRequestException($"OpenAI speech output failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) throw new ProviderRequestException("OpenAI speech output was empty.", (int)response.StatusCode);
        return new SpeechOutputResult(Provider, Model, _voice, "wav", requestId, bytes, DateTimeOffset.UtcNow);
    }
}
