using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>送信状況の入口が、出してよいものだけを出すこと。</summary>
/// <remarks>
/// **DB へは繋がない。** ここで確かめたいのは、
/// 応答の組み立てと SQL の書き方であって、DB の振る舞いではない。
/// </remarks>
public class AdminOutboxTests
{
    private static DateTime Unspecified(string text) =>
        DateTime.SpecifyKind(
            DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Unspecified);

    private static DeadLetterView Row(
        string token = "tok-1",
        string? title = "アンケート",
        string? lastError = "Pleasanter が応答しない") =>
        new(
            token,
            Guid.NewGuid(),
            title,
            SurveyVersion: 3,
            RetryCount: 20,
            lastError,
            CreatedAt: Unspecified("2026-08-18T01:00:00"),
            UpdatedAt: Unspecified("2026-08-19T02:00:00"));

    // ---- 添付を弾いた記録（Issue #39）------------------------------------------

    private static AttachmentRejectionView Rejected(
        string? questionId = "q1",
        int reason = 0,
        int fileCount = 1,
        string? title = "アンケート") =>
        new(Unspecified("2026-08-20T03:00:00"), Guid.NewGuid(), title, questionId, reason, fileCount);

    [Fact]
    public void 弾いた記録の時刻はUTCとして返す()
    {
        var response = AdminOutboxEndpoints.ToResponse([Rejected()], take: 10, recentCount: 5);

        var entry = Assert.Single(response.Entries);
        Assert.Equal(DateTimeKind.Utc, entry.OccurredAt.Kind);
        Assert.Equal(5, response.RecentCount);
        Assert.False(response.HasMore);
    }

    [Fact]
    public void 弾いた記録に送信元もファイル名も入る場所が無い()
    {
        // **約束を注意書きではなく型で守る**（回答者は完全匿名）
        var names = typeof(AttachmentRejectionResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("IpAddress", names);
        Assert.DoesNotContain("FileName", names);
    }

    [Fact]
    public void 一件多く読んだら次のページがあると分かる()
    {
        // **総数は数えない**（増え続ける表を毎回数えないため）
        var response = AdminOutboxEndpoints.ToResponse(
            [Rejected(), Rejected(), Rejected()], take: 2, recentCount: 3);

        Assert.Equal(2, response.Entries.Count);
        Assert.True(response.HasMore);
    }

    [Fact]
    public void 設問に紐づかない理由は設問を空で返す()
    {
        var response = AdminOutboxEndpoints.ToResponse(
            [Rejected(questionId: null, reason: 4)], take: 10, recentCount: 1);

        Assert.Null(Assert.Single(response.Entries).QuestionId);
    }

    // ---- 滞留による受付停止（Issue #72）--------------------------------------

    [Fact]
    public void 止めていることを応答に載せる()
    {
        // **件数からは「止めている」ことが読み取れない。**
        // 送信待ちが多いのと、そのせいで受付を止めているのは別のこと
        var response = AdminOutboxEndpoints.ToResponse(
            new OutboxStatus(50_000, null, 0, null),
            new BacklogGuardStatus(
                Enabled: true,
                Total: 50_000,
                TotalLimit: 50_000,
                TotalBlocked: true,
                PerSurveyLimit: 10_000,
                BlockedSurveyCount: 2,
                SampledAt: new DateTimeOffset(2026, 8, 20, 3, 0, 0, TimeSpan.Zero)));

        var backlog = response.Backlog;
        Assert.NotNull(backlog);
        Assert.True(backlog.TotalBlocked);
        Assert.Equal(2, backlog.BlockedSurveyCount);
        Assert.Equal(DateTimeKind.Utc, backlog.SampledAt!.Value.Kind);
    }

    [Fact]
    public void 一度も数えていなければ計測時刻を載せない()
    {
        // **null は落として返る**（画面側は省略可で受ける）
        var response = AdminOutboxEndpoints.ToResponse(
            new OutboxStatus(0, null, 0, null),
            new BacklogGuardStatus(true, 0, 50_000, false, 10_000, 0, null));

        Assert.NotNull(response.Backlog);
        Assert.Null(response.Backlog.SampledAt);
    }

    // ---- 滞留の状況 ---------------------------------------------------------

    [Fact]
    public void 滞留の時刻はUTCとして返す()
    {
        // **DB の列は時間帯を持たない。** 印を付けずに返すと、
        // 画面が端末の時間帯として読み、日本では 9 時間ずれる
        var response = AdminOutboxEndpoints.ToResponse(new OutboxStatus(
            PendingCount: 5,
            OldestPendingAt: Unspecified("2026-08-19T00:30:00"),
            DeadLetterCount: 2,
            OldestDeadLetterAt: Unspecified("2026-08-17T23:00:00")));

        Assert.Equal(5, response.PendingCount);
        Assert.Equal(2, response.DeadLetterCount);

        Assert.Equal(DateTimeKind.Utc, response.OldestPendingAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, response.OldestDeadLetterAt!.Value.Kind);

        // **値そのものは動かさない。** 印を付け直すだけ
        Assert.Equal(
            new DateTime(2026, 8, 19, 0, 30, 0, DateTimeKind.Utc), response.OldestPendingAt);
    }

    [Fact]
    public void 溜まっていなければ滞留の時刻は無い()
    {
        // **0 件のときに「1970 年から詰まっている」と読めてしまわないこと**
        var response = AdminOutboxEndpoints.ToResponse(
            new OutboxStatus(0, null, 0, null));

        Assert.Null(response.OldestPendingAt);
        Assert.Null(response.OldestDeadLetterAt);
    }

    // ---- デッドレターの一覧 -------------------------------------------------

    [Fact]
    public void 回答本文を返す場所が無い()
    {
        // **注意書きではなく型で守る**（Issue #45）。
        // 本文を持たせようとした時点で、この試験が落ちる
        foreach (var type in new[] { typeof(DeadLetterView), typeof(DeadLetterResponse) })
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Answer", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void 一件多く読んだ分は返さず次があるとだけ伝える()
    {
        // **総数は数えない。** 増え続ける表を画面を開くたびに数えないため
        var page = AdminOutboxEndpoints.ToResponse(
            [Row("tok-1"), Row("tok-2"), Row("tok-3")], take: 2);

        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.HasMore);
        Assert.Equal(["tok-1", "tok-2"], page.Entries.Select(entry => entry.ResponseToken));
    }

    [Fact]
    public void 最後のページでは次があると言わない()
    {
        var page = AdminOutboxEndpoints.ToResponse([Row("tok-1")], take: 2);

        Assert.Single(page.Entries);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void 消えたアンケートの回答も一覧に出す()
    {
        // **アンケートが消えていても、届いていない回答は見えなければならない。**
        // 題名が無いことは画面側で補う
        var page = AdminOutboxEndpoints.ToResponse([Row(title: null)], take: 10);

        var entry = Assert.Single(page.Entries);
        Assert.Null(entry.SurveyTitle);
        Assert.Equal(DateTimeKind.Utc, entry.ReceivedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, entry.LastAttemptAt.Kind);
    }

    [Fact]
    public void 失敗の理由はそのまま渡す()
    {
        // **管理者には原因を見せる**（`_documents/画面設計.md` 3 章）。
        // 回答者へ内部のエラーを出さないのとは別の話
        var page = AdminOutboxEndpoints.ToResponse(
            [Row(lastError: "マッピングの不備: ClassA:桁溢れ")], take: 10);

        Assert.Equal("マッピングの不備: ClassA:桁溢れ", Assert.Single(page.Entries).LastError);
    }

    // ---- SQL の書き方 -------------------------------------------------------

    public static TheoryData<DatabaseProvider> Providers() =>
        new(DatabaseProvider.SqlServer, DatabaseProvider.PostgreSql, DatabaseProvider.MySql);

    [Theory]
    [MemberData(nameof(Providers))]
    public void 滞留の状況は一度の問い合わせで読む(DatabaseProvider provider)
    {
        var sql = SqlDialect.Format(provider, SqlDialect.OutboxStatus);

        // **表を 1 回しか見ない。** 件数と最古の時刻を別々に数えない
        Assert.Equal(1, CountOf(sql, "SELECT"));
        Assert.Equal(1, CountOf(sql, "FROM"));

        // **値は引数で渡す。** 状態の数字を SQL へ書き込まない
        Assert.Contains("@DeadLetterStatus", sql, StringComparison.Ordinal);
        AssertQuoted(provider, sql);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void デッドレターの一覧に回答本文を選ばない(DatabaseProvider provider)
    {
        var sql = SqlDialect.Format(provider, SqlDialect.ListDeadLetters(provider));

        Assert.DoesNotContain("PayloadJson", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", sql, StringComparison.Ordinal);
        Assert.Contains("@DeadLetterStatus", sql, StringComparison.Ordinal);
        AssertQuoted(provider, sql);
    }

    [Fact]
    public void 件数を絞る句は方言ごとに変わる()
    {
        // **MySQL は OFFSET/FETCH を解さない。** 3 者で書き分ける
        Assert.Contains(
            "OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY",
            SqlDialect.ListDeadLetters(DatabaseProvider.SqlServer),
            StringComparison.Ordinal);

        foreach (var provider in new[] { DatabaseProvider.PostgreSql, DatabaseProvider.MySql })
        {
            Assert.Contains(
                "LIMIT @Limit OFFSET @Offset",
                SqlDialect.ListDeadLetters(provider),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 並びを二本の列で決める(DatabaseProvider provider)
    {
        // **時刻は秒までしか持たない**（DbTime）。1 本だけだとページ送りで取りこぼす
        var sql = SqlDialect.ListDeadLetters(provider);

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("[UpdatedAt] DESC, r.[ResponseToken] DESC", sql, StringComparison.Ordinal);
    }

    /// <summary>その RDBMS の引用符になっていること。**囲み忘れが残らないこと。**</summary>
    private static void AssertQuoted(DatabaseProvider provider, string sql)
    {
        if (provider is DatabaseProvider.SqlServer)
        {
            // 角括弧がそのまま引用符
            Assert.Contains("[Responses]", sql, StringComparison.Ordinal);
            return;
        }

        // **書き換え漏れが 1 つも無いこと**
        Assert.DoesNotContain("[", sql, StringComparison.Ordinal);
        Assert.Contains(
            SqlDialect.Quote(provider, "Responses"), sql, StringComparison.Ordinal);
    }

    private static int CountOf(string sql, string keyword)
    {
        var count = 0;
        var index = sql.IndexOf(keyword, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = sql.IndexOf(keyword, index + keyword.Length, StringComparison.Ordinal);
        }

        return count;
    }
}

/// <summary>DB から読んだ時刻に印を付け直すこと。</summary>
public class DbTimeAsUtcTests
{
    [Fact]
    public void 種別だけを付け替える()
    {
        var read = new DateTime(2026, 8, 19, 3, 0, 0, DateTimeKind.Unspecified);

        var marked = DbTime.AsUtc(read);

        Assert.Equal(DateTimeKind.Utc, marked.Kind);
        Assert.Equal(read.Ticks, marked.Ticks);
    }

    [Fact]
    public void 未設定はそのまま()
    {
        Assert.Null(DbTime.AsUtc((DateTime?)null));
    }
}
