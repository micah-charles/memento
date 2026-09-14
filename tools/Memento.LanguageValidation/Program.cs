using System.Text.Json;
using System.Text.Json.Serialization;
using Memento.Core.Validation;

var outputPath = ParseOutputPath(args);
var samples = LanguageValidationCorpus.SyntheticCases.Select(testCase =>
    (testCase, new LanguageValidationObservation(
        testCase.ExpectedTranscript,
        testCase.ExpectedEntities,
        100,
        testCase.RequiresUncertaintyPreservation)));
var report = LanguageValidationHarness.Evaluate(samples);
var document = new
{
    SchemaVersion = 1,
    Corpus = "m04-synthetic-v1",
    GeneratedAtUtc = DateTimeOffset.UtcNow,
    TotalCases = report.Results.Count,
    DispositionCounts = Enum.GetValues<ValidationDisposition>().ToDictionary(disposition => disposition.ToString(), report.Count),
    CorrectionRequiredCount = report.CorrectionRequiredCount,
    Results = report.Results
};
var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
});

if (outputPath is null)
{
    Console.WriteLine(json);
}
else
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (directory is not null) Directory.CreateDirectory(directory);
    File.WriteAllText(fullPath, json + Environment.NewLine);
    Console.WriteLine($"Wrote synthetic language validation report: {fullPath}");
}

return 0;

static string? ParseOutputPath(string[] arguments)
{
    if (arguments.Length == 0) return null;
    if (arguments.Length == 2 && string.Equals(arguments[0], "--output", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments[1]))
        return arguments[1];
    throw new ArgumentException("Usage: dotnet run --project tools/Memento.LanguageValidation -- [--output <path>]");
}
