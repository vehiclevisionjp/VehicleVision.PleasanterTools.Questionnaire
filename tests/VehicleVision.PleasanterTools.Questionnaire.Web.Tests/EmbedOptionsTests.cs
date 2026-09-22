using System.Collections.Frozen;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>埋め込みを許す配信元の設定（Issue #104 / #107）。</summary>
public class EmbedOptionsTests
{
    private static AppSettingsSnapshot Snapshot(string? allowedHosts) =>
        new(
            [],
            new Dictionary<string, string>
            {
                [EmbedOptions.AllowedHostsKey] = allowedHosts ?? string.Empty,
            }.ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty);

    [Fact]
    public void 既定では埋め込みを使えない()
    {
        // **設定しなければ、設定を入れる前と同じ振る舞いになる**
        var options = EmbedOptions.FromSnapshot(Snapshot(null));

        Assert.False(options.Enabled);
        Assert.Empty(options.AllowedHosts);
        Assert.Empty(options.CspSources);
        Assert.False(options.IsAllowed("https://www.example.com/e"));
    }

    [Fact]
    public void 空文字も何も許さないものとして読む()
    {
        var options = EmbedOptions.FromSnapshot(Snapshot("  "));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 読点区切りで複数書ける()
    {
        var options = EmbedOptions.FromSnapshot(
            Snapshot("www.example.com, *.example.net"));

        Assert.True(options.Enabled);
        Assert.Equal(["www.example.com", "*.example.net"], options.AllowedHosts.ToArray());
        Assert.True(options.IsAllowed("https://www.example.com/e"));
        Assert.True(options.IsAllowed("https://video.example.net/e"));
        Assert.False(options.IsAllowed("https://example.net/e"));
        Assert.False(options.IsAllowed("http://www.example.com/e"));
    }

    [Fact]
    public void CSPのホスト源へ直したものを持つ()
    {
        var options = EmbedOptions.FromSnapshot(
            Snapshot("www.example.com,*.example.net"));

        Assert.Equal(
            ["https://www.example.com", "https://*.example.net"],
            options.CspSources.ToArray());
    }
}
