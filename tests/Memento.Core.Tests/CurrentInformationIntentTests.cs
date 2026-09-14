using Memento.Core.External;

namespace Memento.Core.Tests;

public sealed class CurrentInformationIntentTests
{
    [Theory]
    [InlineData("今日香港落唔落雨呀？", CurrentInformationIntent.Weather)]
    [InlineData("而家搭地鐵去荃灣有冇交通問題？", CurrentInformationIntent.Transport)]
    [InlineData("今日有冇最新新聞？", CurrentInformationIntent.News)]
    [InlineData("今晚煮雞有冇簡單食譜？", CurrentInformationIntent.Recipe)]
    public void Explicit_current_question_is_routed(string transcript, CurrentInformationIntent expected)
    {
        Assert.True(CurrentInformationIntentDetector.TryDetect(transcript, out var match));
        Assert.Equal(expected, match.Intent);
        Assert.Equal(transcript, match.Query);
    }

    [Theory]
    [InlineData("我今日唔想出街。")]
    [InlineData("我最怕落雨。")]
    [InlineData("尋日新聞講到阿貞。")]
    [InlineData("你記唔記得我細個住邊？")]
    public void Personal_or_historical_statement_is_not_routed(string transcript)
        => Assert.False(CurrentInformationIntentDetector.TryDetect(transcript, out _));

    [Fact]
    public void Empty_input_is_not_routed()
        => Assert.False(CurrentInformationIntentDetector.TryDetect(" ", out _));
}
