using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>公開時にルックアップが版へ固定されることを 4 RDBMS で確かめる（Issue #822）。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class SurveyLookupsSnapshotTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static Question Q(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese(id),
    };

    private static LookupDefinition Lookup(int maxResults) => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "a1",
        SourceSiteId = 10,
        CredentialId = "cred-1",
        Keys = [new LookupKey { QuestionId = "k1", SourceColumn = "ClassA" }],
        Targets = [new LookupTarget { SourceColumn = "ClassB", QuestionId = "t1" }],
        AllowedColumns = ["ClassB"],
        MaxResults = maxResults,
    };

    private static SurveyDefinition Definition(Guid surveyId, LookupDefinition? lookup) => new()
    {
        SurveyId = surveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages = [new Page { PageId = "p1", Questions = [Q("a1"), Q("k1"), Q("t1")] }],
        Lookups = lookup is null ? null : [lookup],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開した版はルックアップを固定し_下書きの変更で書き換わらない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var drafts = new SurveyDraftStore(factory);
        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, $"pub-{Guid.NewGuid():N}", "検証用", PleasanterSiteId: 1,
            ResponseJsonColumn: null, Status: (int)SurveyStatus.Draft, PublishedVersion: null));

        var revision = await drafts.SaveAsync(
            surveyId, Definition(surveyId, Lookup(5)), new MappingDefinition(), expectedRevision: 0);
        var draft = await drafts.LoadAsync(surveyId);
        await surveys.PublishAsync(surveyId, 1, draft!.Definition, draft.Mapping, null);

        // 公開後に下書きを直す（件数の上限を変える）
        await drafts.SaveAsync(
            surveyId, Definition(surveyId, Lookup(9)), new MappingDefinition(), revision);

        var published = await snapshots.FindAsync(surveyId, 1);

        var lookup = Assert.Single(published!.Definition.GetLookups());
        Assert.Equal(5, lookup.MaxResults);
        Assert.Equal("cred-1", lookup.CredentialId);
        var latest = await drafts.LoadAsync(surveyId);
        Assert.Equal(9, Assert.Single(latest!.Definition.GetLookups()).MaxResults);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ルックアップの無い定義の版は変わらない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var drafts = new SurveyDraftStore(factory);
        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);

        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, $"pub-{Guid.NewGuid():N}", "検証用", PleasanterSiteId: 1,
            ResponseJsonColumn: null, Status: (int)SurveyStatus.Draft, PublishedVersion: null));
        await drafts.SaveAsync(
            surveyId, Definition(surveyId, null), new MappingDefinition(), expectedRevision: 0);
        var draft = await drafts.LoadAsync(surveyId);

        await surveys.PublishAsync(surveyId, 1, draft!.Definition, draft.Mapping, null);

        var published = await snapshots.FindAsync(surveyId, 1);
        Assert.Null(published!.Definition.Lookups);
        Assert.DoesNotContain("lookups", SurveyJson.Serialize(published.Definition), StringComparison.Ordinal);
    }
}
