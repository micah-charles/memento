using System.Net.Http.Headers;
using System.Text.Json;

namespace Memento.Core.Conversation;

public sealed record TranscriptionResult(string Provider, string Model, string? RequestId, string Text, DateTimeOffset CompletedAt);

public interface IApiCredentialProvider
{
    string? GetApiKey();
}

public sealed class DelegateApiCredentialProvider(Func<string?> resolver) : IApiCredentialProvider
{
    public string? GetApiKey() => resolver();
}

public sealed class ProviderRequestException(string message, int statusCode) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Optional completed-audio adapter. It never persists credentials or raw error bodies.</summary>
public sealed class OpenAiTranscriptionProvider : ITranscriptionProvider
{
    private readonly HttpClient _httpClient;
    private readonly IApiCredentialProvider _credentials;

    public OpenAiTranscriptionProvider(HttpClient httpClient, IApiCredentialProvider credentials, string model = "gpt-transcribe")
    {
        _httpClient = httpClient;
        _credentials = credentials;
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("A model is required.", nameof(model)) : model;
    }

    public string Provider => "openai";
    public string Model { get; }

    public async Task<TranscriptionResult> TranscribeAsync(string localAudioPath, string? language = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localAudioPath)) throw new FileNotFoundException("Local audio source is required before transcription.", localAudioPath);
        var apiKey = _credentials.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("An OpenAI API credential is not configured.");
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(localAudioPath);
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(localAudioPath));
        form.Add(new StringContent(Model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.openai.com/v1/audio/transcriptions")) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode)
            throw new ProviderRequestException($"OpenAI transcription failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ProviderRequestException("OpenAI transcription response was not a JSON object.", (int)response.StatusCode);
        if (!document.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new ProviderRequestException("OpenAI transcription response did not contain text.", (int)response.StatusCode);
        return new TranscriptionResult(Provider, Model, requestId, text.GetString() ?? string.Empty, DateTimeOffset.UtcNow);
    }
}
