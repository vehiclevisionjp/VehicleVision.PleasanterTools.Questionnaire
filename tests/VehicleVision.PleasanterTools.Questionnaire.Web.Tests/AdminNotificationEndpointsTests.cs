using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理者への知らせを応答の形にするところ（Issue #80）。</summary>
/// <remarks>
/// **DB へは繋がない。** 確かめたいのは、画面へ渡すときの見え方
/// （時刻の印・種類の名前・紐づかないアンケートの扱い・次のページの有無）。
/// </remarks>
public class AdminNotificationEndpointsTests
{
    private static readonly DateTime Occurred = new(2026, 8, 21, 3, 0, 0, DateTimeKind.Unspecified);

    private static AdminNotificationView View(
        int kind = 1,
        Guid? surveyId = null,
        string? title = null,
        int count = 1,
        DateTime? readAt = null,
        string? lookupId = null,
        string? correlationId = null) =>
        new(Guid.NewGuid(),
            kind,
            surveyId ?? Guid.NewGuid(),
            title,
            count,
            Occurred,
            Occurred.AddMinutes(5),
            readAt,
            lookupId,
            correlationId);

    [Fact]
    public void 時刻に協定世界時の印を付ける()
    {
        // **DB の列は時間帯を持たない。** そのまま返すと画面が端末の時間帯として読む
        var response = AdminNotificationEndpoints.ToResponse(View(readAt: Occurred.AddHours(1)));

        Assert.Equal(DateTimeKind.Utc, response.FirstOccurredAt.Kind);
        Assert.Equal(DateTimeKind.Utc, response.LastOccurredAt.Kind);
        Assert.Equal(DateTimeKind.Utc, response.ReadAt!.Value.Kind);
    }

    [Fact]
    public void 検索IDと相関IDを返し_無い知らせでは落として返す()
    {
        // Issue #836。サーバは null のプロパティを落として返す（画面側の型は省略可）
        var with = AdminNotificationEndpoints.ToResponse(
            View(kind: 12, lookupId: "l1", correlationId: "0HN:0001"));
        Assert.Equal("l1", with.LookupId);
        Assert.Equal("0HN:0001", with.CorrelationId);

        var without = AdminNotificationEndpoints.ToResponse(View(kind: 1));
        Assert.Null(without.LookupId);
        Assert.Null(without.CorrelationId);

        var json = System.Text.Json.JsonSerializer.Serialize(
            without,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
        Assert.DoesNotContain("lookupId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("correlationId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 種類は名前で返す()
    {
        // **画面が整数の意味を知っている状態にしない**
        Assert.Equal("DeadLettered", AdminNotificationEndpoints.ToResponse(View(kind: 1)).Kind);
        Assert.Equal(
            "PleasanterUnauthorized",
            AdminNotificationEndpoints.ToResponse(View(kind: 4)).Kind);
    }

    [Fact]
    public void 知らない種類でも一覧を壊さない()
    {
        // **古い版のアプリが新しい種類を読んでも、一覧全体が開けなくなるより良い**
        Assert.Equal("Unknown", AdminNotificationEndpoints.ToResponse(View(kind: 999)).Kind);
    }

    [Fact]
    public void 紐づかないアンケートは無い物として返す()
    {
        // **DB では Guid.Empty で入っている**（NULL にすると突き合わせが RDBMS ごとに割れる）
        var response = AdminNotificationEndpoints.ToResponse(View(surveyId: Guid.Empty));

        Assert.Null(response.SurveyId);
    }

    [Fact]
    public void 次のページがあるかを数え直さずに返す()
    {
        // **1 件多く読んでいる**ので、返すのは take 件まで
        var rows = Enumerable.Range(0, 4).Select(_ => View()).ToList();

        var page = AdminNotificationEndpoints.ToResponse(rows, take: 3, unreadCount: 7);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasMore);
        // **未読は一覧と別に数える。** ページを送っても数字は変わらない
        Assert.Equal(7, page.UnreadCount);
    }

    [Fact]
    public void 最後のページでは続きが無いと返す()
    {
        var rows = Enumerable.Range(0, 2).Select(_ => View()).ToList();

        Assert.False(AdminNotificationEndpoints.ToResponse(rows, take: 3, unreadCount: 0).HasMore);
    }
}
