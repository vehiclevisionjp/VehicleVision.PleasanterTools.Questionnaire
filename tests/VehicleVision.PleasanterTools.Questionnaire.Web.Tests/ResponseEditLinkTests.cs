using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>再編集リンクのトークンと URL（Issue #202）。</summary>
public class ResponseEditLinkTests
{
    [Fact]
    public void 作るたびに違う値になる()
    {
        Assert.NotEqual(ResponseEditLink.Create(), ResponseEditLink.Create());
    }

    [Fact]
    public void URLに載せても壊れない形で作る()
    {
        // **`+` `/` `=` を含まない**（断片に置くため）
        var token = ResponseEditLink.Create();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void 同じトークンは同じハッシュになる()
    {
        Assert.Equal(ResponseEditLink.HashOf("tok-1"), ResponseEditLink.HashOf("tok-1"));
    }

    [Fact]
    public void ハッシュに元の値を残さない()
    {
        // **表を読めた人が使えては意味が無い**
        Assert.DoesNotContain("tok-1", ResponseEditLink.HashOf("tok-1"), StringComparison.Ordinal);
    }

    [Fact]
    public void トークンは断片へ置く()
    {
        // ⚠️ **断片はサーバへ送られない。** アクセスログにも監査ログにも残らない
        var url = ResponseEditLink.UrlOf("https://survey.example.jp", "pub-1", "tok-1");

        Assert.Equal("https://survey.example.jp/f/pub-1#e=tok-1", url);
    }

    [Fact]
    public void 起点の末尾のスラッシュを重ねない()
    {
        Assert.Equal(
            "https://survey.example.jp/f/pub-1#e=tok-1",
            ResponseEditLink.UrlOf("https://survey.example.jp/", "pub-1", "tok-1"));
    }

    [Fact]
    public void 記号は逃がす()
    {
        var url = ResponseEditLink.UrlOf("https://survey.example.jp", "pub 1", "tok+1/2");

        Assert.Equal("https://survey.example.jp/f/pub%201#e=tok%2B1%2F2", url);
    }
}
