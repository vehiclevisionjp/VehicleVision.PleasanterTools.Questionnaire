using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>設定の検証の例外が、要求の言語で文言を返すこと（Issue #605）。</summary>
public class AppSettingValidationMessageTests
{
    [Fact]
    public void 鍵を持つ例外は要求の言語の文言を返す()
    {
        var exception = AppSettingValidationException.Of(
            ServerMessageKeys.SettingFixedExternally, "ALTCHA_MAX");

        Assert.Contains("ALTCHA_MAX", exception.MessageFor("en"), StringComparison.Ordinal);
        Assert.Contains("fixed by external", exception.MessageFor("en"), StringComparison.Ordinal);
        Assert.Contains("外部設定で固定", exception.MessageFor("ja"), StringComparison.Ordinal);
        Assert.NotEqual(exception.MessageFor("ja"), exception.MessageFor("de"));
    }

    [Fact]
    public void Messageは日本語のまま残る()
    {
        var exception = AppSettingValidationException.Of(ServerMessageKeys.ProofOfWorkRangeInvalid);

        Assert.Equal(exception.MessageFor("ja"), exception.Message);
        Assert.Equal(ServerMessageKeys.ProofOfWorkRangeInvalid, exception.MessageKey);
    }

    [Fact]
    public void 鍵を持たない例外はMessageをそのまま返す()
    {
        var exception = new AppSettingValidationException("直書きの文言");

        Assert.Null(exception.MessageKey);
        Assert.Equal("直書きの文言", exception.MessageFor("en"));
    }

    [Fact]
    public void 知らない言語は英語へ落ちる()
    {
        var exception = AppSettingValidationException.Of(ServerMessageKeys.SettingKeyNotManaged, "X");

        Assert.Equal(exception.MessageFor("en"), exception.MessageFor("xx"));
    }

    private static AppSettingDefinition Definition(
        AppSettingValueType type,
        int? minimum = null,
        int? maximum = null,
        int? maximumLength = null) =>
        new("TEST_KEY", type, string.Empty,
            Minimum: minimum, Maximum: maximum, MaximumLength: maximumLength);

    [Fact]
    public void 型の検証は設定の鍵を差し込んだ要求の言語の文言を返す()
    {
        var cases = new (string Key, Action Act, string English)[]
        {
            (ServerMessageKeys.SettingValueTooLong,
                () => Definition(AppSettingValueType.String, maximumLength: 3).Normalize("abcd"),
                "TEST_KEY: enter 3 characters or fewer."),
            (ServerMessageKeys.SettingNotBoolean,
                () => Definition(AppSettingValueType.Boolean).Normalize("yes"),
                "TEST_KEY: specify true or false."),
            (ServerMessageKeys.SettingNotInteger,
                () => Definition(AppSettingValueType.Integer).Normalize("x"),
                "TEST_KEY: specify an integer."),
            (ServerMessageKeys.SettingBelowMinimum,
                () => Definition(AppSettingValueType.Integer, minimum: 1, maximum: 10).Normalize("0"),
                "TEST_KEY: specify 1 or more."),
            (ServerMessageKeys.SettingAboveMaximum,
                () => Definition(AppSettingValueType.Integer, minimum: 1, maximum: 10).Normalize("11"),
                "TEST_KEY: specify 10 or less."),
        };

        foreach (var (key, act, english) in cases)
        {
            var exception = Assert.Throws<AppSettingValidationException>(act);

            Assert.Equal(key, exception.MessageKey);
            Assert.Equal(english, exception.MessageFor("en"));
            Assert.Contains("TEST_KEY", exception.MessageFor("ja"), StringComparison.Ordinal);
            Assert.NotEqual(exception.MessageFor("en"), exception.MessageFor("ja"));
        }
    }
}
