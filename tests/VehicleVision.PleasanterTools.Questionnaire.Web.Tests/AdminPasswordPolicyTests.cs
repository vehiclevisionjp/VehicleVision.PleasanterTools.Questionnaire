using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理者の合言葉の下限。</summary>
public class AdminPasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("           ")]
    public void 無い入力や短い入力は受け付けない(string? password)
    {
        Assert.False(AdminPasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void 最低長の一文字手前は受け付けない()
    {
        var password = new string('a', AdminPasswordPolicy.MinimumLength - 1);

        Assert.False(AdminPasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void 最低長ちょうどなら受け付ける()
    {
        var password = new string('a', AdminPasswordPolicy.MinimumLength);

        Assert.True(AdminPasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void 最低長を超えれば受け付ける()
    {
        var password = new string('a', AdminPasswordPolicy.MinimumLength + 1);

        Assert.True(AdminPasswordPolicy.IsAcceptable(password));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 短すぎる文言には最低長が入る(string language)
    {
        var message = AdminPasswordPolicy.Message(language);

        Assert.Contains(AdminPasswordPolicy.MinimumLength.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 対応していない言語は既定の文言へ落ちる()
    {
        Assert.Equal(
            AdminPasswordPolicy.Message(SupportedLanguages.Default),
            AdminPasswordPolicy.Message("fr"));
    }
}
