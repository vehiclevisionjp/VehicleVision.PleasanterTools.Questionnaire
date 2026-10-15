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
}
