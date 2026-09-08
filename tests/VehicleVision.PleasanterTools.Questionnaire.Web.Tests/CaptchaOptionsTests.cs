using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>外部の CAPTCHA の設定（Issue #164）。</summary>
/// <remarks>
/// **既定で外部へ出ないこと**と、**秘密鍵が画面へ渡らない形になっていること**が肝。
/// </remarks>
public class CaptchaOptionsTests
{
    private static CaptchaOptions FromPairs(params (string Key, string? Value)[] pairs) =>
        CaptchaOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
                .Build());

    [Fact]
    public void 既定は自前設置()
    {
        var options = FromPairs();

        Assert.Equal(CaptchaProvider.Altcha, options.Provider);
        Assert.False(options.UsesExternalService);
        Assert.False(options.IsExternalReady);
        Assert.Empty(options.CspSources);
        Assert.Null(options.ScriptUrl);
        Assert.Null(options.VerifyUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("recaptcha-v3")]
    public void 知らない値は自前設置へ落とす(string provider)
    {
        // **書き間違いで弱くならない。** 落とす先が「外部を使わない」なので、
        // 自前の課題は動いたまま
        var options = FromPairs(
            ("CaptchaProvider", provider),
            ("CaptchaSiteKey", "site"),
            ("CaptchaSecretKey", "secret"));

        Assert.Equal(CaptchaProvider.Altcha, options.Provider);
        Assert.False(options.UsesExternalService);
    }

    [Theory]
    [InlineData("Recaptcha")]
    [InlineData("turnstile")]
    [InlineData("HCAPTCHA")]
    public void 大文字小文字は問わない(string provider)
    {
        var options = FromPairs(
            ("CaptchaProvider", provider),
            ("CaptchaSiteKey", "site"),
            ("CaptchaSecretKey", "secret"));

        Assert.True(options.UsesExternalService);
        Assert.True(options.IsExternalReady);
    }

    [Theory]
    [InlineData("site", null)]
    [InlineData(null, "secret")]
    [InlineData(null, null)]
    public void 鍵が片方でも欠けていれば使えない(string? siteKey, string? secretKey)
    {
        // **半端な状態で画面へ出さない**
        var options = FromPairs(
            ("CaptchaProvider", "Turnstile"),
            ("CaptchaSiteKey", siteKey),
            ("CaptchaSecretKey", secretKey));

        Assert.True(options.UsesExternalService);
        Assert.False(options.IsExternalReady);
        Assert.Empty(options.CspSources);
    }

    [Theory]
    [InlineData(CaptchaProvider.Recaptcha, "https://www.google.com")]
    [InlineData(CaptchaProvider.Turnstile, "https://challenges.cloudflare.com")]
    [InlineData(CaptchaProvider.Hcaptcha, "https://js.hcaptcha.com")]
    public void 配信元は決め打ち(CaptchaProvider provider, string expected)
    {
        var options = new CaptchaOptions { Provider = provider, SiteKey = "site", SecretKey = "secret" };

        Assert.Equal(expected, options.ScriptOrigin);
        Assert.StartsWith(expected, options.ScriptUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaptchaProvider.Recaptcha, "https://www.google.com/recaptcha/api/siteverify")]
    [InlineData(CaptchaProvider.Turnstile, "https://challenges.cloudflare.com/turnstile/v0/siteverify")]
    [InlineData(CaptchaProvider.Hcaptcha, "https://api.hcaptcha.com/siteverify")]
    public void 検証先も決め打ち(CaptchaProvider provider, string expected)
    {
        var options = new CaptchaOptions { Provider = provider, SiteKey = "site", SecretKey = "secret" };

        Assert.Equal(expected, options.VerifyUrl);
    }

    [Fact]
    public void 明示的に描く形で読み込む()
    {
        // **自動で描かせない。** 差し込む場所と件数をこちらで決める
        var options = new CaptchaOptions
        {
            Provider = CaptchaProvider.Turnstile,
            SiteKey = "site",
            SecretKey = "secret",
        };

        Assert.Contains("render=explicit", options.ScriptUrl!, StringComparison.Ordinal);
    }

    [Fact]
    public void CSPは選んだサービスだけを許す()
    {
        var options = new CaptchaOptions
        {
            Provider = CaptchaProvider.Turnstile,
            SiteKey = "site",
            SecretKey = "secret",
        };

        Assert.Equal("https://challenges.cloudflare.com", Assert.Single(options.CspSources));

        // ⚠️ **`https:` のようには広げない**
        Assert.DoesNotContain("https:", options.CspSources);
        Assert.DoesNotContain("*", options.CspSources);
    }

    [Fact]
    public void reCAPTCHAは部品の配信元も要る()
    {
        var options = new CaptchaOptions
        {
            Provider = CaptchaProvider.Recaptcha,
            SiteKey = "site",
            SecretKey = "secret",
        };

        Assert.Contains("https://www.google.com", options.CspSources);
        Assert.Contains("https://www.gstatic.com", options.CspSources);
    }
}
