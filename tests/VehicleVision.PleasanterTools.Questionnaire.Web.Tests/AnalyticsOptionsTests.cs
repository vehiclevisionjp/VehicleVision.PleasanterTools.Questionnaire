using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>アクセス解析の設定（Issue #162）。</summary>
/// <remarks>
/// **既定で無効であること**と、**半端な設定で読み込ませないこと**が肝。
/// </remarks>
public class AnalyticsOptionsTests
{
    private static AnalyticsOptions FromPairs(params (string Key, string? Value)[] pairs) =>
        AnalyticsOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(pairs.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
                .Build());

    [Fact]
    public void 既定は無効()
    {
        var options = FromPairs();

        Assert.Equal(AnalyticsProvider.None, options.Provider);
        Assert.False(options.IsEnabled);
        Assert.Empty(options.CspSources);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("ga5")]
    public void 知らない値は無効として扱う(string provider)
    {
        // **起動は止めない。** 解析が無くても回答は取れる
        var options = FromPairs(("AnalyticsProvider", provider), ("AnalyticsSiteId", "G-XXXX"));

        Assert.Equal(AnalyticsProvider.None, options.Provider);
        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void 大文字小文字は問わない()
    {
        var options = FromPairs(("AnalyticsProvider", "ga4"), ("AnalyticsSiteId", "G-XXXX"));

        Assert.Equal(AnalyticsProvider.Ga4, options.Provider);
        Assert.True(options.IsEnabled);
    }

    [Fact]
    public void IDが無ければ無効()
    {
        // **半端な設定で読み込ませない**
        var options = FromPairs(("AnalyticsProvider", "Ga4"));

        Assert.False(options.IsEnabled);
        Assert.Empty(options.CspSources);
    }

    [Fact]
    public void GA4は決まった配信元を使う()
    {
        var options = FromPairs(
            ("AnalyticsProvider", "Ga4"),
            ("AnalyticsSiteId", "G-XXXX"),
            // **指定しても見ない**（Google の配信元は決まっている）
            ("AnalyticsScriptOrigin", "https://evil.example.com"));

        Assert.Equal("https://www.googletagmanager.com", options.ResolvedOrigin());
        Assert.DoesNotContain("https://evil.example.com", options.CspSources);
    }

    [Fact]
    public void GA4は計測の送信先も許す()
    {
        var options = FromPairs(("AnalyticsProvider", "Ga4"), ("AnalyticsSiteId", "G-XXXX"));

        Assert.Contains("https://www.googletagmanager.com", options.CspSources);
        Assert.Contains("https://*.google-analytics.com", options.CspSources);

        // ⚠️ **`https:` のようには広げない**
        Assert.DoesNotContain("https:", options.CspSources);
        Assert.DoesNotContain("*", options.CspSources);
    }

    [Fact]
    public void Matomoは配信元が要る()
    {
        Assert.False(FromPairs(("AnalyticsProvider", "Matomo"), ("AnalyticsSiteId", "1")).IsEnabled);

        var options = FromPairs(
            ("AnalyticsProvider", "Matomo"),
            ("AnalyticsSiteId", "1"),
            ("AnalyticsScriptOrigin", "https://matomo.example.jp/"));

        Assert.True(options.IsEnabled);

        // **末尾のスラッシュは落とす**
        Assert.Equal("https://matomo.example.jp", options.ResolvedOrigin());
        Assert.Equal("https://matomo.example.jp", Assert.Single(options.CspSources));
    }

    [Fact]
    public void Plausibleは省略すると本家を使う()
    {
        var options = FromPairs(("AnalyticsProvider", "Plausible"), ("AnalyticsSiteId", "example.jp"));

        Assert.True(options.IsEnabled);
        Assert.Equal("https://plausible.io", options.ResolvedOrigin());
    }

    [Theory]
    [InlineData("matomo.example.jp")]
    [InlineData("/matomo")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://matomo.example.jp")]
    public void 配信元が絶対URLでなければ無効(string origin)
    {
        // **書き間違いを黙って通さない**
        var options = FromPairs(
            ("AnalyticsProvider", "Matomo"),
            ("AnalyticsSiteId", "1"),
            ("AnalyticsScriptOrigin", origin));

        Assert.False(options.IsEnabled);
        Assert.Null(options.ResolvedOrigin());
    }

    [Fact]
    public void パスや問い合わせは落として配信元だけ残す()
    {
        var options = FromPairs(
            ("AnalyticsProvider", "Matomo"),
            ("AnalyticsSiteId", "1"),
            ("AnalyticsScriptOrigin", "https://matomo.example.jp/analytics/?x=1"));

        Assert.Equal("https://matomo.example.jp", options.ResolvedOrigin());
    }

    [Fact]
    public void 告知は既定で出す()
    {
        var options = FromPairs(("AnalyticsProvider", "Ga4"), ("AnalyticsSiteId", "G-XXXX"));

        Assert.True(options.ShowNotice);
    }

    [Fact]
    public void 告知は切れる()
    {
        var options = FromPairs(
            ("AnalyticsProvider", "Ga4"),
            ("AnalyticsSiteId", "G-XXXX"),
            ("AnalyticsShowNotice", "false"));

        Assert.False(options.ShowNotice);
    }
}
