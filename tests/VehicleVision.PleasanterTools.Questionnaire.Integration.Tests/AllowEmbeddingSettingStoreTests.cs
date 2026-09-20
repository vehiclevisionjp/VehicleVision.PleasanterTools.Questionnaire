using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>アンケートごとの回答画面埋め込み可否を、3 RDBMS で確かめる（Issue #334）。</summary>
public class AllowEmbeddingSettingStoreTests
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
        "埋め込みの検証",
        1,
        null,
        (int)SurveyStatus.Published,
        1);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 既定は埋め込みを許可しない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId));

        var record = await surveys.FindBySurveyIdAsync(surveyId);

        Assert.NotNull(record);
        Assert.False(record.AllowEmbedding);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 許可を入れて読み直しても入っている(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        var record = Published(surveyId);
        await surveys.SaveAsync(record with { AllowEmbedding = true });

        Assert.True((await surveys.FindBySurveyIdAsync(surveyId))!.AllowEmbedding);
        Assert.True((await surveys.FindByPublicIdAsync(record.PublicId))!.AllowEmbedding);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 別の公開設定を保存し直しても許可が落ちない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var surveys = new SurveyRepository(Create(provider, connectionString));
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId) with { AllowEmbedding = true });

        var loaded = await surveys.FindBySurveyIdAsync(surveyId);
        Assert.NotNull(loaded);
        await surveys.SaveAsync(loaded with { ResponseLimit = 10 });

        Assert.True((await surveys.FindBySurveyIdAsync(surveyId))!.AllowEmbedding);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧にも許可が出る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = Create(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var drafts = new SurveyDraftStore(factory);
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Published(surveyId) with { AllowEmbedding = true });

        var summary = (await drafts.ListAsync(new SurveyListQuery()))
            .SingleOrDefault(row => row.SurveyId == surveyId);

        Assert.NotNull(summary);
        Assert.True(summary.AllowEmbedding);
    }
}
