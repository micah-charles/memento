namespace Memento.Core.Validation;

/// <summary>
/// Non-sensitive developer-created cases for exercising every M04 category.
/// These cases measure the harness wiring only; they are not evidence of
/// provider accuracy for Cantonese or any other language.
/// </summary>
public static class LanguageValidationCorpus
{
    public static IReadOnlyList<LanguageValidationCase> SyntheticCases { get; } =
    [
        new("yue-001", LanguageValidationCategory.HongKongCantonese, "我今日去飲茶", []),
        new("yue-002", LanguageValidationCategory.ColloquialCantonese, "你食咗飯未呀", []),
        new("zh-001", LanguageValidationCategory.Mandarin, "我今天要去上班", []),
        new("mix-zh-001", LanguageValidationCategory.CantoneseMandarin, "我聽日要返工，但是有啲攰", [], true),
        new("mix-en-001", LanguageValidationCategory.CantoneseEnglish, "我唔記得個 product name", ["product name"], true, true),
        new("name-001", LanguageValidationCategory.Name, "我細個朋友叫阿貞", ["阿貞"]),
        new("place-001", LanguageValidationCategory.PlaceName, "我哋喺沙田見", ["沙田"]),
        new("incomplete-001", LanguageValidationCategory.IncompleteSentence, "如果聽日落雨就", []),
        new("repeat-001", LanguageValidationCategory.Repetition, "我我我想飲茶", []),
        new("hesitation-001", LanguageValidationCategory.Hesitation, "嗯我諗下先", []),
        new("uncertainty-001", LanguageValidationCategory.Uncertainty, "可能係星期三", [], false, true),
        new("number-001", LanguageValidationCategory.Number, "我有三個蘋果", ["三個"]),
        new("date-001", LanguageValidationCategory.Date, "大約二零二零年", ["二零二零年"]),
        new("product-001", LanguageValidationCategory.EnglishProductName, "我部 Microsoft Surface 壞咗", ["Microsoft Surface"], true)
    ];
}
