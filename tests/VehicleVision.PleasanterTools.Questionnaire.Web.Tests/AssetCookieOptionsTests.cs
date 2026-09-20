using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>配布資産の Cookie 属性（Issue #334）。</summary>
public class AssetCookieOptionsTests
{
    [Fact]
    public void 埋め込み不可のアンケートはHTTPSでもLaxのまま()
    {
        var options = FormEndpoints.AssetCookieOptions(
            allowEmbedding: false,
            isHttps: true,
            publicId: "pub-1");

        Assert.True(options.Secure);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
    }

    [Fact]
    public void 平文HTTPではSecureを付けずLaxのまま()
    {
        var options = FormEndpoints.AssetCookieOptions(
            allowEmbedding: true,
            isHttps: false,
            publicId: "pub-1");

        Assert.False(options.Secure);
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
    }

    [Fact]
    public void 埋め込み許可済みかつHTTPSならNoneとSecureを使う()
    {
        var options = FormEndpoints.AssetCookieOptions(
            allowEmbedding: true,
            isHttps: true,
            publicId: "pub-1");

        Assert.True(options.Secure);
        Assert.Equal(SameSiteMode.None, options.SameSite);
    }
}
