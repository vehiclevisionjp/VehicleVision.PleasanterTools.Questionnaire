using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>アンケート一覧をページに切って返すところ（Issue #79）。</summary>
/// <remarks>
/// **DB へは繋がない。** 確かめたいのは、**総数を数えずに「次がある」を返す**という
/// 一点（アンケートは消さずに溜まるので、開くたびに全件を数えない）。
/// </remarks>
public class AdminSurveyListTests
{
    private static SurveySummary Summary() =>
        new(Guid.NewGuid(),
            "PublicId",
            "題名",
            1L,
            0,
            null,
            new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc),
            null,
            null,
            null,
            0,
            false,
            false);

    [Fact]
    public void 次のページがあるかを数え直さずに返す()
    {
        // **1 件多く読んでいる**ので、返すのは take 件まで
        var rows = Enumerable.Range(0, 4).Select(_ => Summary()).ToList();

        var page = AdminSurveyEndpoints.ToResponse(rows, take: 3);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void 最後のページでは続きが無いと返す()
    {
        var rows = Enumerable.Range(0, 2).Select(_ => Summary()).ToList();

        Assert.False(AdminSurveyEndpoints.ToResponse(rows, take: 3).HasMore);
    }

    [Fact]
    public void 一件も無くても壊れない()
    {
        var page = AdminSurveyEndpoints.ToResponse([], take: 3);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void 絞り込みの既定は何も掛けない()
    {
        // **指定が無ければ全部**（状態も題名も条件にしない）
        var query = new SurveyListQuery();

        Assert.Null(query.Status);
        Assert.Null(query.TitleContains);
        Assert.Equal(0, query.Offset);
    }
}
