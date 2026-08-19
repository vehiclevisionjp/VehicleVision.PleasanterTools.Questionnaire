using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>回答数の上限と停止の理由を、3 RDBMS で確かめる（Issue #53）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// 起動は <c>docker compose --profile all up -d --wait</c>。
/// </para>
/// <para>
/// **ここで確かめたいのは、単体試験では代わりが効かない 3 つ。**
/// <c>EnsureAsync</c> が「作った / 既にあった」を 3 者とも同じ行数で返すこと、
/// <c>SuspendForResponseLimitAsync</c> が公開中の行しか止めないこと、
/// 一覧の受付数が相関副問い合わせで取れること。
/// </para>
/// </remarks>
public class ResponseLimitStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static DbConnectionFactory Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        return new DbConnectionFactory(provider, connectionString);
    }

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    private static SurveyRecord Published(Guid surveyId, int? responseLimit = null) => new(
        surveyId,
        $"pub-{Guid.NewGuid():N}",
        "上限の検証",
        1,
        null,
        (int)SurveyStatus.Published,
        1,
        ResponseLimit: responseLimit);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 作ったときだけ真を返す(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **3 者とも「作ったら 1 行、既にあったら 0 行」でなければならない。**
        // ここがずれると、回答の編集が受付数を増やしてしまう
        var tokens = new ResponseTokenStore(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        var token = NewToken();

        Assert.True(await tokens.EnsureAsync(token, surveyId));
        Assert.False(await tokens.EnsureAsync(token, surveyId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 既にあるReferenceIdを消さない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **編集が Create になり二重登録になるのを防ぐ約束。**
        // 「作ったか」を返すようにした後も守れていることを見る
        var tokens = new ResponseTokenStore(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        var token = NewToken();

        await tokens.SaveAsync(token, surveyId, 4321);
        Assert.False(await tokens.EnsureAsync(token, surveyId));

        Assert.Equal(4321, await tokens.FindReferenceIdAsync(token));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 受付数はアンケートごとに数える(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var tokens = new ResponseTokenStore(Create(provider, connectionString));
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();

        await tokens.EnsureAsync(NewToken(), mine);
        await tokens.EnsureAsync(NewToken(), mine);
        await tokens.EnsureAsync(NewToken(), other);

        Assert.Equal(2, await tokens.CountAcceptedAsync(mine));
        Assert.Equal(1, await tokens.CountAcceptedAsync(other));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 自動停止は公開中の行だけを止める(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId, responseLimit: 1));

        Assert.True(await surveys.SuspendForResponseLimitAsync(surveyId));

        var suspended = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(suspended);
        Assert.Equal((int)SurveyStatus.Suspended, suspended.Status);
        Assert.Equal((int)SurveySuspendedReason.ResponseLimitReached, suspended.SuspendedReason);
        Assert.NotNull(suspended.SuspendedAt);

        // **2 度目は 0 行。** 何度呼んでも同じ結果になる
        Assert.False(await surveys.SuspendForResponseLimitAsync(surveyId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 手で止めた理由を自動停止が上書きしない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();

        await surveys.SaveAsync(Published(surveyId, responseLimit: 1) with
        {
            Status = (int)SurveyStatus.Suspended,
            SuspendedReason = (int)SurveySuspendedReason.Manual,
            SuspendedAt = DbTime.UtcNowTruncated(),
        });

        Assert.False(await surveys.SuspendForResponseLimitAsync(surveyId));

        var record = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(record);

        // **なぜ止まっているのかが変わってはいけない**
        Assert.Equal((int)SurveySuspendedReason.Manual, record.SuspendedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 下書きへ戻した行を自動停止で止めない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();

        await surveys.SaveAsync(
            Published(surveyId, responseLimit: 1) with { Status = (int)SurveyStatus.Draft });

        Assert.False(await surveys.SuspendForResponseLimitAsync(surveyId));

        var record = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(record);
        Assert.Equal((int)SurveyStatus.Draft, record.Status);
        Assert.Null(record.SuspendedReason);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧に受付数と上限と停止の理由が出る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = Create(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var tokens = new ResponseTokenStore(factory);
        var drafts = new SurveyDraftStore(factory);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId, responseLimit: 5));
        await tokens.EnsureAsync(NewToken(), surveyId);
        await tokens.EnsureAsync(NewToken(), surveyId);
        await surveys.SuspendForResponseLimitAsync(surveyId);

        var summary = (await drafts.ListAsync())
            .Single(row => row.SurveyId == surveyId);

        // **相関副問い合わせで数えている。** COUNT の型差で落ちないことも同時に見る
        Assert.Equal(2, summary.ResponseCount);
        Assert.Equal(5, summary.ResponseLimit);
        Assert.Equal((int)SurveySuspendedReason.ResponseLimitReached, summary.SuspendedReason);
        Assert.NotNull(summary.SuspendedAt);
    }
}
