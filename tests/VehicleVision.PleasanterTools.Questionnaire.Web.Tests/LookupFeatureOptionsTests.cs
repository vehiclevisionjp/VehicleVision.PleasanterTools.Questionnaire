using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public sealed class LookupFeatureOptionsTests
{
    [Fact]
    public void 未設定ならルックアップを有効にしない()
    {
        Assert.False(Read().Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void 空なら有効にしない(string value)
    {
        Assert.False(Read((LookupFeatureOptions.EnabledSetting, value)).Enabled);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" True ", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void 真偽値を読み取る(string value, bool expected)
    {
        Assert.Equal(expected, Read((LookupFeatureOptions.EnabledSetting, value)).Enabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("enabled")]
    public void 真偽値でない値なら_設定名つきで起動を止める(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Read((LookupFeatureOptions.EnabledSetting, value)));

        Assert.Contains("QUESTIONNAIRE_LOOKUP_ENABLED", exception.Message, StringComparison.Ordinal);
        // 値そのものはメッセージへ出さない
        Assert.DoesNotContain($"'{value}'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 設定名は環境変数の流儀どおり()
    {
        Assert.Equal("QUESTIONNAIRE_LOOKUP_ENABLED", LookupFeatureOptions.EnabledSetting);
    }

    private static LookupFeatureOptions Read(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();
        return LookupFeatureOptions.FromConfiguration(configuration);
    }
}
