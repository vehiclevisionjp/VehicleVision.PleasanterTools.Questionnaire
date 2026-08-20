using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>送信状況とデッドレターの読み書きを 3 RDBMS で確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **ここが無いと SQL が実機で一度も動かない。** 送信状況の集約は
/// <c>COUNT(CASE …)</c> と <c>MIN(CASE …)</c>、一覧は <c>LEFT JOIN</c> ＋ 件数を絞る句で、
/// **いずれも 3 者で書き方や型が割れる場所**（Issue #45）。
/// </para>
/// </remarks>
public class OutboxStatusTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static async Task<(IResponseOutbox Outbox, ISurveyRepository Surveys)> CreateAsync(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **前の試験の行を消してから始める。** 件数を数えるので、残っていると揺れる
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            SqlDialect.Format(provider, "DELETE FROM [Responses]")).ConfigureAwait(false);

        return (new ResponseOutbox(factory), new SurveyRepository(factory));
    }

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    /// <summary>デッドレターを 1 件作る。</summary>
    private static async Task<string> DeadLetterAsync(IResponseOutbox outbox, Guid surveyId)
    {
        var token = NewToken();
        await outbox.SaveAsync(token, surveyId, 1, """{"a":1}""").ConfigureAwait(false);
        await outbox.DeadLetterAsync(token, "恒久的な失敗").ConfigureAwait(false);
        return token;
    }

    // ---- 滞留の見張りが数える件数（Issue #72）--------------------------------

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 滞留の総数にはデッドレターも入る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **自然に捌けないデッドレターを除くと、
        // 「送信待ちは 0 件なのに DB が溢れる」が起きる**
        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await outbox.SaveAsync(NewToken(), surveyId, 1, """{"a":1}""").ConfigureAwait(true);
        await DeadLetterAsync(outbox, surveyId).ConfigureAwait(true);

        var backlog = await outbox.CountBacklogAsync(1).ConfigureAwait(true);

        Assert.Equal(2, backlog.Total);
        Assert.Equal(2, backlog.For(surveyId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 下限に満たないアンケートは返らない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **危ない水準のものだけを返す。** 全アンケートぶん返すと、
        // 本数に比例して見張りの費用が上がる
        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var many = Guid.NewGuid();
        var few = Guid.NewGuid();

        for (var i = 0; i < 3; i++)
        {
            await outbox.SaveAsync(NewToken(), many, 1, """{"a":1}""").ConfigureAwait(true);
        }

        await outbox.SaveAsync(NewToken(), few, 1, """{"a":1}""").ConfigureAwait(true);

        var backlog = await outbox.CountBacklogAsync(3).ConfigureAwait(true);

        // **総数は絞り込みの影響を受けない。** 受けると全体の段が効かなくなる
        Assert.Equal(4, backlog.Total);
        Assert.Equal(3, backlog.For(many));
        Assert.Equal(0, backlog.For(few));
        Assert.False(backlog.BySurvey.ContainsKey(few));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 送信できた分は滞留から消える(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **捌けたら受け付け直せること**を、行の消え方として確かめる
        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();
        var token = NewToken();

        await outbox.SaveAsync(token, surveyId, 1, """{"a":1}""").ConfigureAwait(true);
        Assert.Equal(1, (await outbox.CountBacklogAsync(1).ConfigureAwait(true)).Total);

        await outbox.CompleteAsync(token).ConfigureAwait(true);

        Assert.Equal(0, (await outbox.CountBacklogAsync(1).ConfigureAwait(true)).Total);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 滞留が無ければ空で返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **空の表で GROUP BY を取ると 0 行。** 例外にしない
        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        var backlog = await outbox.CountBacklogAsync(1).ConfigureAwait(true);

        Assert.Equal(0, backlog.Total);
        Assert.Empty(backlog.BySurvey);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 何も無ければ零件で時刻は無い(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        var status = await outbox.GetStatusAsync().ConfigureAwait(true);

        // **空の表で MIN を取ると NULL。** int で受けていると RDBMS によってだけ落ちる
        Assert.Equal(0, status.PendingCount);
        Assert.Equal(0, status.DeadLetterCount);
        Assert.Null(status.OldestPendingAt);
        Assert.Null(status.OldestDeadLetterAt);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 未送信とデッドレターを数え分ける(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        await outbox.SaveAsync(NewToken(), surveyId, 1, """{"a":1}""").ConfigureAwait(true);
        await outbox.SaveAsync(NewToken(), surveyId, 1, """{"a":2}""").ConfigureAwait(true);
        await DeadLetterAsync(outbox, surveyId).ConfigureAwait(true);

        var status = await outbox.GetStatusAsync().ConfigureAwait(true);

        Assert.Equal(2, status.PendingCount);
        Assert.Equal(1, status.DeadLetterCount);

        // **「何件あるか」より「いつから詰まっているか」の方が異常に気付ける**
        Assert.NotNull(status.OldestPendingAt);
        Assert.NotNull(status.OldestDeadLetterAt);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task デッドレターを題名つきで読める(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, surveys) = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId,
            $"pub-{Guid.NewGuid():N}",
            "滞留の見本",
            PleasanterSiteId: 1,
            ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Published,
            PublishedVersion: 1)).ConfigureAwait(true);

        var token = await DeadLetterAsync(outbox, surveyId).ConfigureAwait(true);

        var row = Assert.Single(
            await outbox.ListDeadLettersAsync(new DeadLetterQuery()).ConfigureAwait(true));

        Assert.Equal(token, row.ResponseToken);
        Assert.Equal(surveyId, row.SurveyId);
        Assert.Equal("滞留の見本", row.SurveyTitle);
        Assert.Equal("恒久的な失敗", row.LastError);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 消えたアンケートの滞留も見える(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        // **アンケートの行が無い状態。** 内部結合にすると、この滞留がまるごと見えなくなる
        await DeadLetterAsync(outbox, Guid.NewGuid()).ConfigureAwait(true);

        var row = Assert.Single(
            await outbox.ListDeadLettersAsync(new DeadLetterQuery()).ConfigureAwait(true));

        Assert.Null(row.SurveyTitle);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧をページで送れる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();

        for (var index = 0; index < 5; index++)
        {
            await DeadLetterAsync(outbox, surveyId).ConfigureAwait(true);
        }

        var first = await outbox
            .ListDeadLettersAsync(new DeadLetterQuery { Limit = 2 }).ConfigureAwait(true);
        var second = await outbox
            .ListDeadLettersAsync(new DeadLetterQuery { Limit = 2, Offset = 2 }).ConfigureAwait(true);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);

        // **取りこぼしも重複も無いこと。** 時刻は秒までしか持たないので、
        // 並びを 1 本の列だけで決めていると同じ行が 2 度出る
        Assert.Empty(first.Select(row => row.ResponseToken)
            .Intersect(second.Select(row => row.ResponseToken), StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 手で戻すと送信待ちへ帰る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);
        var surveyId = Guid.NewGuid();
        var token = await DeadLetterAsync(outbox, surveyId).ConfigureAwait(true);

        var returned = await outbox.RequeueDeadLetterAsync(token).ConfigureAwait(true);

        // **どのアンケートの回答を戻したかが分かること**（監査ログの対象に使う）
        Assert.Equal(surveyId, returned);

        var status = await outbox.GetStatusAsync().ConfigureAwait(true);
        Assert.Equal(1, status.PendingCount);
        Assert.Equal(0, status.DeadLetterCount);

        // **戻した行はもう一覧に出ない**
        Assert.Empty(await outbox.ListDeadLettersAsync(new DeadLetterQuery()).ConfigureAwait(true));

        // **本当に送れる状態か。** 状態だけ変えて確保できないと、戻したことにならない
        var claimed = await outbox
            .ClaimAsync("worker-1", TimeSpan.FromMinutes(5)).ConfigureAwait(true);
        Assert.NotNull(claimed);
        Assert.Equal(token, claimed.ResponseToken);
        Assert.Equal(0, claimed.RetryCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task デッドレターでない行は戻せない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (outbox, _) = await CreateAsync(provider, connectionString).ConfigureAwait(true);

        var token = NewToken();
        await outbox.SaveAsync(token, Guid.NewGuid(), 1, """{"a":1}""").ConfigureAwait(true);

        // **送信待ちの行を「戻す」と、回数が消えて再送の間隔もやり直しになる**
        Assert.Null(await outbox.RequeueDeadLetterAsync(token).ConfigureAwait(true));

        // 知らないトークンでも落ちない
        Assert.Null(await outbox.RequeueDeadLetterAsync(NewToken()).ConfigureAwait(true));
    }
}
