using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>アンケートごとの下書きの可否を、3 RDBMS で確かめる（Issue #59）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **ここで確かめたいのは、単体試験では代わりが効かない 2 つ。**
/// 真偽の列が 3 者で同じように往復すること
/// （MySQL は <c>tinyint(1)</c> で、真偽型そのものを持たない）と、
/// **移行の既定値が <c>false</c> であること。**
/// 既定が <c>true</c> だと、**移行しただけで既存のアンケートが端末へ回答を残し始める。**
/// </para>
/// </remarks>
public class AllowDraftSettingStoreTests
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
        "下書きの検証",
        1,
        null,
        (int)SurveyStatus.Published,
        1);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 既定は残さない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **端末は共有され得る。** 移行しただけで残し始めてはいけない
        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId));

        var record = await surveys.FindBySurveyIdAsync(surveyId);

        Assert.NotNull(record);
        Assert.False(record.AllowDraft);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 入れて読み直しても入っている(
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

        await surveys.SaveAsync(record with { AllowDraft = true });

        var byId = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(byId);
        Assert.True(byId.AllowDraft);

        // **回答画面へ返すのはこちらの経路。** どちらでも同じ値でなければならない
        var byPublicId = await surveys.FindByPublicIdAsync(record.PublicId);
        Assert.NotNull(byPublicId);
        Assert.True(byPublicId.AllowDraft);
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

        // **読んで書き戻す経路で落ちないこと。**
        // 上限だけを直したつもりで下書きが切れると、回答者には理由が分からない
        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId) with { AllowDraft = true });

        var loaded = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(loaded);
        await surveys.SaveAsync(loaded with { ResponseLimit = 10 });

        var again = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(again);
        Assert.True(again.AllowDraft);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧にも旗が出る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **管理画面は一覧から設定の画面を開く。** ここに出ないと今の値が分からず、
        // 開いた拍子に既定へ戻る
        var factory = Create(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var drafts = new SurveyDraftStore(factory);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId) with { AllowDraft = true });

        var summary = (await drafts.ListAsync(new SurveyListQuery()))
            .SingleOrDefault(row => row.SurveyId == surveyId);

        Assert.NotNull(summary);
        Assert.True(summary.AllowDraft);
    }
}
