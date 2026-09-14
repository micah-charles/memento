using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memento.Core.Validation;

/// <summary>
/// Reads a redacted, machine-readable M04 corpus together with the observed
/// provider output. The file is deliberately external to the repository so
/// consented recordings and transcripts are never needed in source control.
/// </summary>
public static class LanguageValidationDataset
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static IReadOnlyList<(LanguageValidationCase Case, LanguageValidationObservation Observation)> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A dataset path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var document = JsonSerializer.Deserialize<InputDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The language validation dataset is empty.");
        if (document.Cases is null || document.Cases.Count == 0)
            throw new InvalidDataException("The language validation dataset must contain at least one case.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var samples = new List<(LanguageValidationCase Case, LanguageValidationObservation Observation)>(document.Cases.Count);
        foreach (var input in document.Cases)
        {
            if (string.IsNullOrWhiteSpace(input.CaseId))
                throw new InvalidDataException("Every language validation case requires a caseId.");
            if (!seenIds.Add(input.CaseId))
                throw new InvalidDataException($"Duplicate language validation caseId: {input.CaseId}");
            if (string.IsNullOrWhiteSpace(input.ExpectedTranscript))
                throw new InvalidDataException($"Case '{input.CaseId}' requires expectedTranscript.");
            if (input.Observation is null)
                throw new InvalidDataException($"Case '{input.CaseId}' requires an observation.");
            if (input.Observation.LatencyMs < 0)
                throw new InvalidDataException($"Case '{input.CaseId}' has a negative latencyMs.");

            var testCase = new LanguageValidationCase(
                input.CaseId,
                input.Category,
                input.ExpectedTranscript,
                input.ExpectedEntities ?? [],
                input.RequiresCodeSwitchPreservation,
                input.RequiresUncertaintyPreservation,
                input.RequiredCodeSwitchSegments);
            var observation = new LanguageValidationObservation(
                input.Observation.ObservedTranscript ?? string.Empty,
                input.Observation.ObservedEntities ?? [],
                input.Observation.LatencyMs,
                input.Observation.UncertaintyPreserved);
            samples.Add((testCase, observation));
        }

        return samples;
    }

    private sealed class InputDocument
    {
        public List<InputCase>? Cases { get; set; }
    }

    private sealed class InputCase
    {
        public string CaseId { get; set; } = string.Empty;
        public LanguageValidationCategory Category { get; set; }
        public string ExpectedTranscript { get; set; } = string.Empty;
        public List<string>? ExpectedEntities { get; set; }
        public bool RequiresCodeSwitchPreservation { get; set; }
        public bool RequiresUncertaintyPreservation { get; set; }
        public List<string>? RequiredCodeSwitchSegments { get; set; }
        public InputObservation? Observation { get; set; }
    }

    private sealed class InputObservation
    {
        public string? ObservedTranscript { get; set; }
        public List<string>? ObservedEntities { get; set; }
        public long LatencyMs { get; set; }
        public bool UncertaintyPreserved { get; set; }
    }
}
