namespace Memento.Core.External;

public enum CurrentInformationIntent
{
    None,
    Weather,
    Transport,
    News,
    Recipe,
    Other
}

public sealed record CurrentInformationIntentMatch(CurrentInformationIntent Intent, string Query);

/// <summary>
/// Conservative, provider-independent routing for spoken current-information
/// requests. It only matches explicit present-time/question cues; personal
/// statements are left on the normal conversation path.
/// </summary>
public static class CurrentInformationIntentDetector
{
    public static bool TryDetect(string? transcript, out CurrentInformationIntentMatch match)
    {
        match = new(CurrentInformationIntent.None, string.Empty);
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        var query = transcript.Trim();
        var text = query.ToLowerInvariant();
        var currentCue = ContainsAny(text, "今日", "而家", "依家", "目前", "最新", "今朝", "今晚", "today", "now", "current", "latest");
        var questionCue = query.Contains('？') || query.Contains('?') || ContainsAny(text, "嗎", "呀", "點", "幾時", "有冇", "係咪", "how", "what", "when", "is there");
        if (!currentCue || !questionCue) return false;

        var intent = ContainsAny(text, "天氣", "落唔落雨", "落雨", "氣溫", "weather", "rain", "temperature")
            ? CurrentInformationIntent.Weather
            : ContainsAny(text, "交通", "地鐵", "巴士", "塞車", "道路", "traffic", "mtr", "bus")
                ? CurrentInformationIntent.Transport
                : ContainsAny(text, "新聞", "消息", "最新消息", "news")
                    ? CurrentInformationIntent.News
                    : ContainsAny(text, "食譜", "點煮", "recipe", "cook")
                        ? CurrentInformationIntent.Recipe
                        : CurrentInformationIntent.Other;

        match = new CurrentInformationIntentMatch(intent, query);
        return true;
    }

    private static bool ContainsAny(string text, params string[] terms)
        => terms.Any(text.Contains);
}
