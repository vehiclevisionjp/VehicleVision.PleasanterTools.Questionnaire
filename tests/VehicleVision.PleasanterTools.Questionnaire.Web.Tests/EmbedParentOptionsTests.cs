using System.Collections.Frozen;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答画面の埋め込みを許す親サイトの設定（Issue #334）。</summary>
public class EmbedParentOptionsTests
{
    private static AppSettingsSnapshot Snapshot(string? allowedParents) =>
        new(
            [],
            new Dictionary<string, string>
            {
                [EmbedParentOptions.AllowedParentsKey] = allowedParents ?? string.Empty,
            }.ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty);

    [Fact]
    public void 既定では親サイトを許可しない()
    {
        var options = EmbedParentOptions.FromSnapshot(Snapshot(null));

        Assert.False(options.Enabled);
        Assert.Empty(options.AllowedParents);
        Assert.Empty(options.CspSources);
    }

    [Fact]
    public void 空文字も親サイトを許可しない()
    {
        var options = EmbedParentOptions.FromSnapshot(Snapshot("  "));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void 読点区切りのホストをCSPのホスト源へ直す()
    {
        var options = EmbedParentOptions.FromSnapshot(
            Snapshot("www.example.com, *.example.net"));

        Assert.True(options.Enabled);
        Assert.Equal(
            ["www.example.com", "*.example.net"],
            options.AllowedParents.ToArray());
        Assert.Equal(
            ["https://www.example.com", "https://*.example.net"],
            options.CspSources.ToArray());
    }

    [Fact]
    public void CSPへ書けない値は許可元から落とす()
    {
        var options = EmbedParentOptions.FromSnapshot(
            Snapshot("www.example.com, bad.example.com' https:"));

        Assert.True(options.Enabled);
        Assert.Equal(["https://www.example.com"], options.CspSources.ToArray());
    }
}
