using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// アンケートごとの proof-of-work の旗を、3 RDBMS で確かめる（Issue #66）。
/// </summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile all up -d --wait</c>。
/// </para>
/// <para>
/// **ここで確かめたいのは、単体試験では代わりが効かない 2 つ。**
/// 真偽の列が 3 者で同じように往復すること
/// （MySQL は <c>tinyint(1)</c> で、真偽型そのものを持たない）と、
/// **移行の既定値が <c>true</c> であること。**
/// 既定が <c>false</c> だと、**移行しただけで既存のアンケートの守りが外れる。**
/// </para>
/// </remarks>
public class ProofOfWorkSettingStoreTests
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

    private static SurveyRecord Published(Guid surveyId) => new(
        surveyId,
        $"pub-{Guid.NewGuid():N}",
        "proof-of-work の検証",
        1,
        null,
        (int)SurveyStatus.Published,
        1);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 既定は要る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **旗を触らずに作ったアンケートは有効。** 公開の窓口に置かれることを前提にする
        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId));

        var record = await surveys.FindBySurveyIdAsync(surveyId);

        Assert.NotNull(record);
        Assert.True(record.RequireProofOfWork);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 切って読み直しても切れている(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **MySQL は真偽型を持たない**（`tinyint(1)`）。3 者で同じように往復するか
        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        var record = Published(surveyId);

        await surveys.SaveAsync(record with { RequireProofOfWork = false });

        var byId = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(byId);
        Assert.False(byId.RequireProofOfWork);

        // **受け付ける側はこちらで引く。** どちらの経路でも同じ値でなければならない
        var byPublicId = await surveys.FindByPublicIdAsync(record.PublicId);
        Assert.NotNull(byPublicId);
        Assert.False(byPublicId.RequireProofOfWork);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開設定を保存し直しても旗が落ちない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **読んで書き戻す経路で落ちないこと**（`IsTemplate` で踏んだ落とし穴と同じ形）。
        // 上限だけを直したつもりで proof-of-work が外れると、気付けない
        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId));

        var loaded = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(loaded);
        await surveys.SaveAsync(loaded with { ResponseLimit = 10 });

        var again = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(again);
        Assert.True(again.RequireProofOfWork);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧にも旗が出る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **管理画面の公開設定は一覧の値を初期値にする。**
        // ここが取れないと、開くたびに「有効」に戻って見える
        var factory = Create(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var drafts = new SurveyDraftStore(factory);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId) with { RequireProofOfWork = false });

        var summary = (await drafts.ListAsync(new SurveyListQuery())).Single(row => row.SurveyId == surveyId);

        Assert.False(summary.RequireProofOfWork);
    }
}
