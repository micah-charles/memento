using System.Globalization;

namespace Memento.Core.Validation;

public enum LanguageValidationCategory
{
    HongKongCantonese,
    ColloquialCantonese,
    Mandarin,
    CantoneseMandarin,
    CantoneseEnglish,
    Name,
    PlaceName,
    IncompleteSentence,
    Repetition,
    Hesitation,
    Uncertainty,
    Number,
    Date,
    EnglishProductName
}

public enum ValidationDisposition
{
    Pass,
    AcceptableWithClarification,
    Weak,
    Fail,
    NotTested
}

public sealed record LanguageValidationCase(
    string CaseId,
    LanguageValidationCategory Category,
    string ExpectedTranscript,
    IReadOnlyList<string> ExpectedEntities,
    bool RequiresCodeSwitchPreservation = false,
    bool RequiresUncertaintyPreservation = false,
    IReadOnlyList<string>? RequiredCodeSwitchSegments = null);

public sealed record LanguageValidationObservation(
    string ObservedTranscript,
    IReadOnlyList<string> ObservedEntities,
    long LatencyMs,
    bool UncertaintyPreserved = false);

public sealed record LanguageValidationResult(
    string CaseId,
    LanguageValidationCategory Category,
    ValidationDisposition Disposition,
    double TranscriptSimilarity,
    double EntityAccuracy,
    bool CodeSwitchPreserved,
    bool UncertaintyPreserved,
    long LatencyMs,
    string? FailureReason,
    bool CorrectionRequired = false);

public sealed record LanguageValidationReport(IReadOnlyList<LanguageValidationResult> Results)
{
    public int Count(ValidationDisposition disposition) => Results.Count(result => result.Disposition == disposition);
    public int CorrectionRequiredCount => Results.Count(result => result.CorrectionRequired);
}

public static partial class LanguageValidationHarness
{
    private static readonly string[] UncertaintyMarkers = ["唔記得", "唔肯定", "可能", "或者", "不確定", "maybe", "perhaps"];

    public static LanguageValidationResult Evaluate(LanguageValidationCase testCase, LanguageValidationObservation observation)
    {
        var expected = Normalize(testCase.ExpectedTranscript);
        var observed = Normalize(observation.ObservedTranscript);
        var similarity = Similarity(expected, observed);
        var expectedEntities = testCase.ExpectedEntities.Select(Normalize).Where(value => value.Length > 0).ToArray();
        var observedEntities = observation.ObservedEntities.Select(Normalize).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        var entityAccuracy = expectedEntities.Length == 0 ? 1d : expectedEntities.Count(observedEntities.Contains) / (double)expectedEntities.Length;
        var requiredSegments = testCase.RequiredCodeSwitchSegments?.Select(Normalize).Where(value => value.Length > 0).ToArray() ?? [];
        var codeSwitch = !testCase.RequiresCodeSwitchPreservation || (HasLatin(expected) == HasLatin(observed)
            && expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(HasLatin).All(observed.Contains)
            && requiredSegments.All(observed.Contains));
        var uncertainty = !testCase.RequiresUncertaintyPreservation || observation.UncertaintyPreserved || UncertaintyMarkers.Any(observed.Contains);

        var disposition = ValidationDisposition.Pass;
        string? failure = null;
        if (string.IsNullOrWhiteSpace(observed))
        {
            disposition = ValidationDisposition.Fail;
            failure = "No transcript was observed.";
        }
        else if (entityAccuracy < 1d)
        {
            disposition = ValidationDisposition.Weak;
            failure = "One or more expected entities were not preserved.";
        }
        else if (!codeSwitch || !uncertainty)
        {
            disposition = ValidationDisposition.AcceptableWithClarification;
            failure = !codeSwitch ? "Code-switch content was not preserved." : "Uncertainty was not preserved.";
        }
        else if (similarity < 0.98d)
        {
            disposition = ValidationDisposition.AcceptableWithClarification;
            failure = "Transcript differs from the synthetic expected wording.";
        }

        var correctionRequired = disposition is ValidationDisposition.Weak or ValidationDisposition.AcceptableWithClarification;
        return new LanguageValidationResult(testCase.CaseId, testCase.Category, disposition, similarity, entityAccuracy, codeSwitch, uncertainty, observation.LatencyMs, failure, correctionRequired);
    }

    public static LanguageValidationReport Evaluate(IEnumerable<(LanguageValidationCase Case, LanguageValidationObservation Observation)> samples)
        => new(samples.Select(sample => Evaluate(sample.Case, sample.Observation)).ToArray());

    private static string Normalize(string value) => value.Trim().ToLower(CultureInfo.InvariantCulture);

    private static double Similarity(string expected, string observed)
    {
        if (expected == observed) return 1d;
        if (expected.Length == 0 || observed.Length == 0) return 0d;
        var distance = Levenshtein(expected, observed);
        return Math.Max(0d, 1d - distance / (double)Math.Max(expected.Length, observed.Length));
    }

    private static bool HasLatin(string value) => value.Any(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
