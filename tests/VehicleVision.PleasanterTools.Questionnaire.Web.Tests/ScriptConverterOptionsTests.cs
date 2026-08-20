using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Scripting;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>変換スクリプトの上限を設定から読む部分の試験。</summary>
/// <remarks>
/// **読む係は <c>.Scripting</c> だが、試験はここに置く**（`ResponseSenderOptions` と同じ理由）。
/// <c>ConfigurationBuilder</c> のために、あちらの試験へ依存を増やしたくない。
/// </remarks>
public class ScriptConverterOptionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Fact]
    public void 既定は二百ミリ秒()
    {
        // **設定しなくても上限が掛かっていること**（Issue #83）
        var options = ScriptConverterOptions.FromConfiguration(Configuration());

        Assert.Equal(TimeSpan.FromMilliseconds(200), options.TimeLimit);
        Assert.Equal(4 * 1024 * 1024, options.MemoryLimitBytes);
        Assert.Equal(64, options.RecursionLimit);
    }

    [Fact]
    public void 上限は設定で変えられる()
    {
        var options = ScriptConverterOptions.FromConfiguration(Configuration(
            (ScriptConverterOptions.TimeLimitKey, "1500"),
            (ScriptConverterOptions.MemoryLimitKey, "1048576"),
            (ScriptConverterOptions.RecursionLimitKey, "16")));

        Assert.Equal(TimeSpan.FromMilliseconds(1500), options.TimeLimit);
        Assert.Equal(1048576, options.MemoryLimitBytes);
        Assert.Equal(16, options.RecursionLimit);
    }

    [Theory]
    [InlineData(ScriptConverterOptions.TimeLimitKey, "ゆっくり")]
    [InlineData(ScriptConverterOptions.TimeLimitKey, "0")]
    [InlineData(ScriptConverterOptions.TimeLimitKey, "-1")]
    [InlineData(ScriptConverterOptions.MemoryLimitKey, "0")]
    [InlineData(ScriptConverterOptions.RecursionLimitKey, "0")]
    public void 上限を外す指定はできない(string key, string value)
    {
        // **0 以下を「無制限」として通すと、この Issue で塞いだ穴がそのまま戻る**
        var exception = Assert.Throws<InvalidOperationException>(
            () => ScriptConverterOptions.FromConfiguration(Configuration((key, value))));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }
}
