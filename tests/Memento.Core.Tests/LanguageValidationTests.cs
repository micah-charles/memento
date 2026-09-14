using Memento.Core.Validation;

namespace Memento.Core.Tests;

public sealed class LanguageValidationTests
{
    [Fact]
    public void Exact_synthetic_cantonese_case_passes()
    {
        var testCase = new LanguageValidationCase("yue-001", LanguageValidationCategory.HongKongCantonese, "我今日去飲茶", [], false, false);
        var result = LanguageValidationHarness.Evaluate(testCase, new LanguageValidationObservation("我今日去飲茶", [], 420));

        Assert.Equal(ValidationDisposition.Pass, result.Disposition);
        Assert.Equal(1d, result.TranscriptSimilarity);
        Assert.Equal(ValidationDisposition.Pass, result.Disposition);
    }

    [Fact]
    public void Name_mismatch_is_weak_even_when_other_words_match()
    {
        var testCase = new LanguageValidationCase("name-001", LanguageValidationCategory.Name, "我朋友叫阿貞", ["阿貞"]);
        var result = LanguageValidationHarness.Evaluate(testCase, new LanguageValidationObservation("我朋友叫阿珍", ["阿珍"], 510));

        Assert.Equal(ValidationDisposition.Weak, result.Disposition);
        Assert.Equal(0d, result.EntityAccuracy);
        Assert.True(result.CorrectionRequired);
    }

    [Fact]
    public void Mixed_language_and_uncertainty_are_measured_separately()
    {
        var testCase = new LanguageValidationCase("mix-001", LanguageValidationCategory.CantoneseEnglish, "我唔記得個 product name", ["product name"], true, true);
        var observation = new LanguageValidationObservation("我唔記得個產品名", ["產品名"], 700, false);
        var result = LanguageValidationHarness.Evaluate(testCase, observation);

        Assert.Equal(ValidationDisposition.Weak, result.Disposition);
        Assert.False(result.CodeSwitchPreserved);
        Assert.True(result.UncertaintyPreserved);
    }

    [Fact]
    public void Report_keeps_case_level_dispositions_and_latency()
    {
        var samples = new[]
        {
            (new LanguageValidationCase("a", LanguageValidationCategory.Mandarin, "你好", [], false, false), new LanguageValidationObservation("你好", [], 100)),
            (new LanguageValidationCase("b", LanguageValidationCategory.Date, "大約二零二零年", [], false, true), new LanguageValidationObservation("二零二零年", [], 120, false))
        };

        var report = LanguageValidationHarness.Evaluate(samples);

        Assert.Equal(2, report.Results.Count);
        Assert.Equal(1, report.Count(ValidationDisposition.Pass));
        Assert.Equal(1, report.Count(ValidationDisposition.AcceptableWithClarification));
        Assert.Equal(1, report.CorrectionRequiredCount);
        Assert.Equal(120, report.Results[1].LatencyMs);
    }

    [Fact]
    public void Synthetic_corpus_covers_every_required_category_without_private_content()
    {
        var cases = LanguageValidationCorpus.SyntheticCases;

        Assert.Equal(Enum.GetValues<LanguageValidationCategory>().Length, cases.Count);
        Assert.Equal(cases.Count, cases.Select(testCase => testCase.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetValues<LanguageValidationCategory>().OrderBy(category => category), cases.Select(testCase => testCase.Category).OrderBy(category => category));

        var report = LanguageValidationHarness.Evaluate(cases.Select(testCase =>
            (testCase, new LanguageValidationObservation(testCase.ExpectedTranscript, testCase.ExpectedEntities, 100, testCase.RequiresUncertaintyPreservation))));

        Assert.Equal(cases.Count, report.Count(ValidationDisposition.Pass));
        Assert.Equal(0, report.CorrectionRequiredCount);
    }

    [Fact]
    public void Code_switch_metric_fails_when_a_required_language_segment_is_missing()
    {
        var testCase = new LanguageValidationCase(
            "mix-zh-missing",
            LanguageValidationCategory.CantoneseMandarin,
            "我聽日要返工，但是有啲攰",
            [],
            true,
            false,
            ["聽日要返工", "但是有啲攰"]);

        var result = LanguageValidationHarness.Evaluate(testCase, new LanguageValidationObservation("我聽日要返工", [], 180));

        Assert.False(result.CodeSwitchPreserved);
        Assert.Equal(ValidationDisposition.AcceptableWithClarification, result.Disposition);
        Assert.True(result.CorrectionRequired);
    }

    [Fact]
    public void External_dataset_reader_loads_redacted_provider_observations()
    {
        var path = Path.Combine(Path.GetTempPath(), "memento-language-dataset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
            {
              "cases": [
                {
                  "caseId": "name-live-001",
                  "category": "name",
                  "expectedTranscript": "我朋友叫阿貞",
                  "expectedEntities": ["阿貞"],
                  "observation": {
                    "observedTranscript": "我朋友叫阿珍",
                    "observedEntities": ["阿珍"],
                    "latencyMs": 812
                  }
                }
              ]
            }
            """);

            var samples = LanguageValidationDataset.Read(path);

            var sample = Assert.Single(samples);
            Assert.Equal("name-live-001", sample.Case.CaseId);
            Assert.Equal(LanguageValidationCategory.Name, sample.Case.Category);
            Assert.Equal(812, sample.Observation.LatencyMs);
            Assert.Equal(ValidationDisposition.Weak, LanguageValidationHarness.Evaluate(sample.Case, sample.Observation).Disposition);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void External_dataset_reader_rejects_duplicate_case_ids()
    {
        var path = Path.Combine(Path.GetTempPath(), "memento-language-dataset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
            {
              "cases": [
                { "caseId": "duplicate", "category": "name", "expectedTranscript": "甲", "observation": { "observedTranscript": "甲", "latencyMs": 1 } },
                { "caseId": "duplicate", "category": "name", "expectedTranscript": "乙", "observation": { "observedTranscript": "乙", "latencyMs": 1 } }
              ]
            }
            """);

            var error = Assert.Throws<InvalidDataException>(() => LanguageValidationDataset.Read(path));
            Assert.Contains("Duplicate", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
