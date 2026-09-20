using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理画面へ返す動作中のアプリケーション版を確かめる。</summary>
public class AdminVersionEndpointsTests
{
    [Fact]
    public void 情報版から版と短いコミットIDを返す()
    {
        var response = AdminVersionEndpoints.ToResponse("0.2.0+be9ef5f0123456789abcdef");

        Assert.Equal("0.2.0", response.Version);
        Assert.Equal("be9ef5f01234", response.Commit);
    }

    [Fact]
    public void コミットIDではないビルド情報は返さない()
    {
        var response = AdminVersionEndpoints.ToResponse("0.2.0+ci.123");

        Assert.Equal("0.2.0", response.Version);
        Assert.Null(response.Commit);
    }

    [Fact]
    public void コミットIDを含まない情報版はそのまま返す()
    {
        var response = AdminVersionEndpoints.ToResponse("0.2.0");

        Assert.Equal("0.2.0", response.Version);
        Assert.Null(response.Commit);
        Assert.False(response.AllowInsecure);
        Assert.False(response.UsesSqlite);
    }

    [Fact]
    public void HTTP運用の宣言を管理画面へ返す()
    {
        var response = AdminVersionEndpoints.ToResponse("0.2.0", allowInsecure: true);

        Assert.True(response.AllowInsecure);
    }

    [Fact]
    public void SQLite運用の宣言を管理画面へ返す()
    {
        var response = AdminVersionEndpoints.ToResponse("0.2.0", usesSqlite: true);

        Assert.True(response.UsesSqlite);
    }
}
