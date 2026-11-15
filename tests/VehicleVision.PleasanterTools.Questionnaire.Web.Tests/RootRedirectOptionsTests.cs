using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>トップページの案内先（<c>QUESTIONNAIRE_ROOT_REDIRECT_URL</c>）の検証（Issue #967）。</summary>
public class RootRedirectOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 未設定と空は案内しない(string? value)
    {
        var options = RootRedirectOptions.Parse(value);

        Assert.False(options.IsConfigured);
        Assert.Null(options.Target);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/f/survey-1")]
    [InlineData("/admin")]
    [InlineData("/welcome?lang=ja")]
    [InlineData("/welcome#top")]
    [InlineData("http://example.com")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/path?x=1&y=2#frag")]
    [InlineData("HTTPS://Example.COM/")]
    [InlineData("https://example.com:8443/a")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://vhub-quest.vehiclevision.jp/")]
    public void 使える値はそのまま受け付ける(string value)
    {
        var options = RootRedirectOptions.Parse(value);

        Assert.True(options.IsConfigured);
        Assert.Equal(value, options.Target);
    }

    [Theory]
    [InlineData("//example.com")]
    [InlineData("//example.com/path")]
    [InlineData("///example.com")]
    [InlineData("/\example.com")]
    [InlineData("\\example.com")]
    [InlineData("/a\b")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:a@example.com")]
    [InlineData("example.com")]
    [InlineData("welcome")]
    [InlineData("https:/example.com")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    [InlineData("http://")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://@example.com/")]
    [InlineData("https://example.com@evil.example/")]
    [InlineData(" /welcome")]
    [InlineData("/welcome ")]
    [InlineData("/wel come")]
    [InlineData("https://example.com/a b")]
    [InlineData("/welcome\r\nSet-Cookie: a=b")]
    [InlineData("/welcome\n")]
    [InlineData("/welcome\t")]
    [InlineData("/welcome\u0000")]
    [InlineData("/こんにちは")]
    [InlineData("https://例.jp/")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://[::1")]
    public void 書き間違いや危険な値は起動時に止める(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => RootRedirectOptions.Parse(value));

        // **英語で出す。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）
        Assert.Contains(RootRedirectOptions.Setting, exception.Message, StringComparison.Ordinal);
        Assert.All(exception.Message, character => Assert.True(character < 0x80));
    }

    [Fact]
    public void 長すぎる値は止める()
    {
        var value = "/" + new string('a', RootRedirectOptions.MaximumLength);

        Assert.Throws<InvalidOperationException>(() => RootRedirectOptions.Parse(value));
        Assert.True(RootRedirectOptions.Parse("/" + new string('a', RootRedirectOptions.MaximumLength - 1)).IsConfigured);
    }

    [Fact]
    public void 止めるときの文言へ値を含めない()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => RootRedirectOptions.Parse("javascript:token-9f3a1c"));

        Assert.DoesNotContain("token-9f3a1c", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "/welcome", "/welcome")]
    [InlineData("/questionnaire", "/welcome", "/questionnaire/welcome")]
    [InlineData("/questionnaire", "/", "/questionnaire/")]
    [InlineData("/apps/q", "/f/a?x=1", "/apps/q/f/a?x=1")]
    [InlineData("/questionnaire", "https://example.com/", "https://example.com/")]
    [InlineData("", "http://example.com/a?b=c", "http://example.com/a?b=c")]
    public void サイト内のパスにだけサブパスを足す(string pathBase, string target, string expected)
    {
        var options = RootRedirectOptions.Parse(target);

        Assert.Equal(expected, options.Resolve(new PathString(pathBase)));
    }

    [Fact]
    public void 設定の鍵から読める()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RootRedirectOptions.Setting] = "https://example.com/",
            })
            .Build();

        Assert.Equal("https://example.com/", RootRedirectOptions.FromConfiguration(configuration).Target);
        Assert.False(RootRedirectOptions.FromConfiguration(
            new ConfigurationBuilder().Build()).IsConfigured);
    }

    [Fact]
    public void 未設定のまま解決しようとしたら止める()
    {
        Assert.Throws<InvalidOperationException>(() => RootRedirectOptions.None.Resolve(PathString.Empty));
    }
}
