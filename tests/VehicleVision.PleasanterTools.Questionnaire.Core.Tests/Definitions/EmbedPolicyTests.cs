using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>埋め込みを許す配信元の判定（Issue #104 / #107）。</summary>
public class EmbedPolicyTests
{
    private static readonly string[] Hosts = ["www.example.com", "*.example.net"];

    /// <summary>結果を配列で受ける。</summary>
    /// <remarks>
    /// **<see cref="ImmutableArray{T}"/> のまま <c>Assert.Equal</c> へ渡すと参照比較になる。**
    /// 中身が同じでも失敗するので、配列へ直してから比べる。
    /// </remarks>
    private static string[] ToSources(IEnumerable<string> hosts)
        => [.. EmbedPolicy.ToCspSources(hosts)];

    [Fact]
    public void 設定が空なら何も許さない()
    {
        // **既定は空。** 設定しない限り一切埋め込ませない
        Assert.False(EmbedPolicy.IsAllowed("https://www.example.com/a", []));
        Assert.False(EmbedPolicy.IsAllowed("https://www.example.com/a", null));
    }

    [Fact]
    public void 名指ししたホストは許す()
        => Assert.True(EmbedPolicy.IsAllowed("https://www.example.com/embed/1", Hosts));

    [Fact]
    public void 名指ししたホストの上位ドメインは許さない()
        => Assert.False(EmbedPolicy.IsAllowed("https://example.com/embed/1", Hosts));

    [Fact]
    public void ワイルドカードは下位のホストを許す()
        => Assert.True(EmbedPolicy.IsAllowed("https://video.example.net/e/1", Hosts));

    [Fact]
    public void ワイルドカードは何段でも許す()
        => Assert.True(EmbedPolicy.IsAllowed("https://a.b.example.net/e/1", Hosts));

    [Fact]
    public void ワイルドカードはそのドメイン自体を許さない()
    {
        // **CSP の `*.example.net` と同じ意味にする。**
        // 保存を許す範囲と CSP が通す範囲がずれると
        // 「保存できたのに出ない」という分かりにくい壊れ方をする
        Assert.False(EmbedPolicy.IsAllowed("https://example.net/e/1", Hosts));
        Assert.False(EmbedPolicy.Matches("example.net", "*.example.net"));
    }

    [Fact]
    public void 末尾が同じだけのホストは許さない()
    {
        // `evil-example.net` が `*.example.net` に一致しては困る
        Assert.False(EmbedPolicy.IsAllowed("https://evilexample.net/e", Hosts));
        Assert.False(EmbedPolicy.Matches("evilexample.net", "*.example.net"));
    }

    [Fact]
    public void アスタリスクだけの設定は何にも一致しない()
    {
        // **書き間違いで全部通るのを防ぐ**
        Assert.False(EmbedPolicy.Matches("example.net", "*."));
        Assert.False(EmbedPolicy.IsAllowed("https://example.net/e", ["*."]));
    }

    [Theory]
    [InlineData("http://www.example.com/e")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void https以外は許さない(string? url)
        => Assert.False(EmbedPolicy.IsAllowed(url, Hosts));

    [Fact]
    public void 利用者情報の付いたURLは許さない()
    {
        // **`https://www.example.com@evil.test/` のような紛らわしい URL を作らせない**
        Assert.False(EmbedPolicy.IsAllowed("https://user:pass@www.example.com/e", Hosts));
    }

    [Fact]
    public void ホストの大文字小文字は区別しない()
        => Assert.True(EmbedPolicy.IsAllowed("https://WWW.Example.COM/e", Hosts));

    [Fact]
    public void CSPのホスト源へ直す()
    {
        var sources = ToSources(Hosts);

        Assert.Equal(["https://www.example.com", "https://*.example.net"], sources);
    }

    [Fact]
    public void CSPへ書けない文字を含む設定は落とす()
    {
        // **空白や引用符が混ざるとヘッダの区切りが壊れ、指定そのものが効かなくなる**
        var sources = ToSources(
            ["www.example.com", "bad host.example", "a'b.example", "x;y.example", ""]);

        Assert.Equal(["https://www.example.com"], sources);
    }

    [Fact]
    public void CSPのホスト源はポートを許す()
        => Assert.Equal(["https://www.example.com:8443"],
            ToSources(["www.example.com:8443"]));

    [Fact]
    public void CSPのホスト源は重複を畳む()
        => Assert.Equal(["https://www.example.com"],
            ToSources(["www.example.com", "WWW.EXAMPLE.COM"]));

    [Fact]
    public void 縦横比は収まる値へ落とす()
    {
        var source = new EmbedSource(EmbedKind.Frame, "https://www.example.com/e", AspectRatio: 999);
        Assert.Equal(EmbedSource.MaximumAspectRatio, source.Normalized().AspectRatio);

        var zero = new EmbedSource(EmbedKind.Frame, "https://www.example.com/e", AspectRatio: 0);
        Assert.Equal(EmbedSource.MinimumAspectRatio, zero.Normalized().AspectRatio);

        var nan = new EmbedSource(EmbedKind.Frame, "https://www.example.com/e", AspectRatio: double.NaN);
        Assert.Equal(EmbedSource.DefaultAspectRatio, nan.Normalized().AspectRatio);
    }

    [Fact]
    public void 埋め込みは回答を持たない()
    {
        // **Pleasanter の列へ写さない。** 説明文ブロックと同じ扱い
        var question = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Embed,
            Title = LocalizedText.Japanese("ロゴ"),
        };

        Assert.True(question.IsDisplayOnly);
    }
}

