using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

public class AssetTicketTests
{
    [Fact]
    public void URLは断片で引換券を渡す()
    {
        var url = AssetTicket.UrlOf("https://survey.example.jp/", "pub 1", "tok+1/2");

        Assert.Equal(
            "https://survey.example.jp/f/pub%201#d=tok%2B1%2F2",
            url);
        Assert.DoesNotContain("?d=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 作るたびに違いURLで壊れない()
    {
        var first = AssetTicket.Create();
        var second = AssetTicket.Create();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void 保存用ハッシュへ平文を残さない()
    {
        Assert.DoesNotContain(
            "ticket-1",
            AssetTicket.HashOf("ticket-1"),
            StringComparison.Ordinal);
    }
}
