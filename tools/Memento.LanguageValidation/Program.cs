using System.Text.Json;
using System.Text.Json.Serialization;
using Memento.Core.Validation;

var options = ParseArguments(args);
var samples = options.InputPath is null
    ? LanguageValidationCorpus.SyntheticCases.Select(testCase =>
        (testCase, new LanguageValidationObservation(
            testCase.ExpectedTranscript,
            testCase.ExpectedEntities,
            100,
            testCase.RequiresUncertaintyPreservation)))
        .ToArray()
    : LanguageValidationDataset.Read(options.InputPath);
var report = LanguageValidationHarness.Evaluate(samples);
var document = new
{
    SchemaVersion = 1,
    Corpus = options.CorpusLabel,
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

if (options.OutputPath is null)
{
    Console.WriteLine(json);
}
else
{
    var fullPath = Path.GetFullPath(options.OutputPath);
    var directory = Path.GetDirectoryName(fullPath);
    if (directory is not null) Directory.CreateDirectory(directory);
    File.WriteAllText(fullPath, json + Environment.NewLine);
    Console.WriteLine($"Wrote language validation report: {fullPath}");
}

return 0;

static (string? InputPath, string? OutputPath, string CorpusLabel) ParseArguments(string[] arguments)
{
    string? inputPath = null;
    string? outputPath = null;
    string? corpusLabel = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        var option = arguments[index];
        if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
            throw new ArgumentException("Usage: dotnet run --project tools/Memento.LanguageValidation -- [--input <dataset.json>] [--output <report.json>] [--corpus <label>]");

        var value = arguments[++index];
        if (string.Equals(option, "--input", StringComparison.OrdinalIgnoreCase))
        {
            if (inputPath is not null) throw new ArgumentException("--input may be specified only once.");
            inputPath = value;
        }
        else if (string.Equals(option, "--output", StringComparison.OrdinalIgnoreCase))
        {
            if (outputPath is not null) throw new ArgumentException("--output may be specified only once.");
            outputPath = value;
        }
        else if (string.Equals(option, "--corpus", StringComparison.OrdinalIgnoreCase))
        {
            if (corpusLabel is not null) throw new ArgumentException("--corpus may be specified only once.");
            if (value.Length > 80 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
                throw new ArgumentException("--corpus must be at most 80 characters and contain only letters, numbers, '-', '_', or '.'.");
            corpusLabel = value;
        }
        else
        {
            throw new ArgumentException("Usage: dotnet run --project tools/Memento.LanguageValidation -- [--input <dataset.json>] [--output <report.json>] [--corpus <label>]");
        }
    }

    return (inputPath, outputPath, corpusLabel ?? (inputPath is null ? "m04-synthetic-v1" : "m04-external-redacted-v1"));
}
