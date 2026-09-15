using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

public class LocalizedTextTests
{
    [Fact]
    public void 指定した言語の文字列を返す()
    {
        var text = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "満足度",
            ["en"] = "Satisfaction",
        });

        Assert.Equal("Satisfaction", text.Get("en"));
    }

    [Fact]
    public void 言語コードの大文字小文字を区別しない()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["en"] = "Satisfaction" });

        Assert.Equal("Satisfaction", text.Get("EN"));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData(null)]
    public void 該当が無ければ既定の言語へ落ちる(string? language)
    {
        var text = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "満足度",
            ["en"] = "Satisfaction",
        });

        Assert.Equal("満足度", text.Get(language));
    }

    [Fact]
    public void 既定の言語も無ければ空文字を返す()
    {
        // 例外にしない。設問が 1 つ翻訳漏れしただけで回答画面が落ちるほうが困る
        var text = new LocalizedText(new Dictionary<string, string> { ["en"] = "Satisfaction" });

        Assert.Equal(string.Empty, text.Get("fr"));
    }
}
