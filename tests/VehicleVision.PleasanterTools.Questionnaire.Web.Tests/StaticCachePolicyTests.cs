using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Web;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>画面の配信に付けるキャッシュの指示（Issue #425）。</summary>
/// <remarks>
/// ⚠️ **入口の HTML を長く持たせると、古い資産を指し続ける。**
/// 実際に**作り直した画面が出ず、1 週間気付かなかった。**
/// </remarks>
public class StaticCachePolicyTests
{
    [Theory]
    [InlineData("/assets/admin-BIjxd9Jb.js")]
    [InlineData("/assets/admin-DPWwLWYw.css")]
    [InlineData("/assets/logo-a1b2c3d4.png")]
    public void 内容ハッシュ付きの資産は長く持たせる(string path)
    {
        Assert.Equal(StaticCachePolicy.ImmutableValue, StaticCachePolicy.ValueFor(path));
    }

    [Theory]
    [InlineData("/admin.html")]
    [InlineData("/index.html")]
    [InlineData("/admin")]
    [InlineData("/admin/surveys/2f1c1f3c-0000-0000-0000-000000000000")]
    [InlineData("/f/abcdefgh")]
    public void 入口は毎回確かめさせる(string path)
    {
        Assert.Equal(StaticCachePolicy.RevalidateValue, StaticCachePolicy.ValueFor(path));
    }

    /// <summary>
    /// ⚠️ **`/assets/` の外は名前が変わらない。**
    /// 長く持たせると差し替えが効かなくなる。
    /// </summary>
    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/fonts/NotoSansJP.woff2")]
    public void 名前が変わらないものは長く持たせない(string path)
    {
        Assert.Equal(StaticCachePolicy.RevalidateValue, StaticCachePolicy.ValueFor(path));
    }

    /// <summary>⚠️ **`/assets/` に置いた HTML は入口になり得る。**</summary>
    [Fact]
    public void assetsの中でもHTMLは毎回確かめさせる()
    {
        Assert.Equal(StaticCachePolicy.RevalidateValue, StaticCachePolicy.ValueFor("/assets/x.html"));
    }

    [Fact]
    public void 応答へ指示を書き込む()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/assets/admin-BIjxd9Jb.js";

        context.Response.Headers.CacheControl =
            StaticCachePolicy.ValueFor(context.Request.Path);

        Assert.Equal(StaticCachePolicy.ImmutableValue, context.Response.Headers.CacheControl);
    }
}
