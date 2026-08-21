using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>要求へ返すサーバ文言の言語。</summary>
public class RequestLanguageTests
{
    private static string Resolve(string? acceptLanguage)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage is not null)
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        return RequestLanguage.Of(context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ヘッダーが無ければ既定の言語になる(string? acceptLanguage)
    {
        Assert.Equal(SupportedLanguages.Default, Resolve(acceptLanguage));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("*")]
    [InlineData("???")]
    public void 対応していない指定は既定の言語になる(string acceptLanguage)
    {
        Assert.Equal(SupportedLanguages.Default, Resolve(acceptLanguage));
    }

    [Fact]
    public void 複数の指定から先に出た対応言語を選ぶ()
    {
        Assert.Equal("en", Resolve("fr, en-US, ja"));
    }

    [Fact]
    public void 重みが大きい対応言語を選ぶ()
    {
        Assert.Equal("ja", Resolve("en;q=0.4, ja;q=0.9"));
    }

    [Fact]
    public void 重みがゼロの言語は選ばない()
    {
        Assert.Equal("en", Resolve("ja;q=0, en;q=0.5"));
    }

    [Fact]
    public void 地域付きの英語は英語として扱う()
    {
        Assert.Equal("en", Resolve("en-GB"));
    }

    [Fact]
    public void 読めない重みは既定値として扱う()
    {
        Assert.Equal("en", Resolve("en;q=not-a-number, ja;q=0.5"));
    }
}
