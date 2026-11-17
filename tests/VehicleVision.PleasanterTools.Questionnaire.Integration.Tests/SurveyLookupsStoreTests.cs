using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>編集中のルックアップ定義の読み書きを 4 RDBMS で確かめる（Issue #820）。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class SurveyLookupsStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (ISurveyDraftStore Drafts, ISurveyRepository Surveys) Create(
        DatabaseProvider provider,
        string connectionString)
    {
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        return (new SurveyDraftStore(factory), new SurveyRepository(factory));
    }

    private static async Task<Guid> CreateSurveyAsync(ISurveyRepository surveys)
    {
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(new SurveyRecord(
            surveyId,
            $"pub-{Guid.NewGuid():N}",
            "検証用",
            PleasanterSiteId: 1,
            ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Draft,
            PublishedVersion: null));
        return surveyId;
    }

    private static Question Q(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese(id),
    };

    private static LookupDefinition Lookup(string id, string anchor, string credential = "cred-1") => new()
    {
        LookupId = id,
        AnchorQuestionId = anchor,
        ButtonLabel = LocalizedText.Japanese("検索して入力"),
        SourceSiteId = 10,
        CredentialId = credential,
        Keys = [new LookupKey { QuestionId = "k1", SourceColumn = "ClassA" }],
        Targets = [new LookupTarget { SourceColumn = "ClassB", QuestionId = "t1" }],
        AllowedColumns = ["ClassB", "ClassC"],
        DisplayFields =
        [
            new LookupDisplayField
            {
                DisplayId = "city",
                SourceColumn = "ClassB",
                Heading = LocalizedText.Japanese("市区町村"),
            },
        ],
        MaxResults = 7,
    };

    private static SurveyDefinition Definition(
        Guid surveyId, params LookupDefinition[] lookups) => new()
    {
        SurveyId = surveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages =
        [
            new Page { PageId = "p1", Questions = [Q("a1"), Q("a2"), Q("k1"), Q("t1")] },
        ],
        Lookups = lookups.Length == 0 ? null : [.. lookups],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ルックアップを保存して読み直せる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, Lookup("l1", "a1"), Lookup("l2", "a2")),
            new MappingDefinition(),
            expectedRevision: 0);

        var loaded = await drafts.LoadAsync(surveyId);

        var lookups = loaded!.Definition.GetLookups();
        Assert.Equal(["l1", "l2"], lookups.Select(lookup => lookup.LookupId).ToArray());
        var first = lookups[0];
        Assert.Equal("a1", first.AnchorQuestionId);
        Assert.Equal(10, first.SourceSiteId);
        Assert.Equal("cred-1", first.CredentialId);
        Assert.Equal("検索して入力", first.ButtonLabel!.Get("ja"));
        Assert.Equal("ClassA", Assert.Single(first.Keys).SourceColumn);
        Assert.Equal("ClassB", Assert.Single(first.Targets).SourceColumn);
        Assert.Equal(["ClassB", "ClassC"], first.AllowedColumns.ToArray());
        var displayField = Assert.Single(first.DisplayFields);
        Assert.Equal("city", displayField.DisplayId);
        Assert.Equal("ClassB", displayField.SourceColumn);
        Assert.Equal("市区町村", displayField.Heading!.Get("ja"));
        Assert.Equal(7, first.MaxResults);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ルックアップが無ければnullで読み直す(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **無いときは null。** 空の配列を返すと、公開した版の JSON に `lookups` が載って変わる
        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId), new MappingDefinition(), expectedRevision: 0);

        var loaded = await drafts.LoadAsync(surveyId);

        Assert.Null(loaded!.Definition.Lookups);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保存し直すと入れ替わり_消したルックアップは残らない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var revision = await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, Lookup("l1", "a1"), Lookup("l2", "a2")),
            new MappingDefinition(),
            expectedRevision: 0);
        revision = await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, Lookup("l2", "a2") with { MaxResults = 3 }),
            new MappingDefinition(),
            expectedRevision: revision);

        var loaded = await drafts.LoadAsync(surveyId);

        var lookup = Assert.Single(loaded!.Definition.GetLookups());
        Assert.Equal("l2", lookup.LookupId);
        Assert.Equal(3, lookup.MaxResults);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId), new MappingDefinition(), expectedRevision: revision);
        Assert.Null((await drafts.LoadAsync(surveyId))!.Definition.Lookups);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 複製してもルックアップは写るが資格情報の参照は引き継がない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);
        await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, Lookup("l1", "a1")),
            new MappingDefinition(),
            expectedRevision: 0);

        var target = new SurveyDuplicationTarget(
            Guid.NewGuid(), $"pub-{Guid.NewGuid():N}", PleasanterSiteId: 999, ResponseJsonColumn: null);
        Assert.True(await drafts.DuplicateAsync(surveyId, target));

        var copy = await drafts.LoadAsync(target.SurveyId);

        var lookup = Assert.Single(copy!.Definition.GetLookups());
        Assert.Equal("l1", lookup.LookupId);
        Assert.Equal(10, lookup.SourceSiteId);
        Assert.Null(lookup.CredentialId);

        // 元は変わらない
        var original = await drafts.LoadAsync(surveyId);
        Assert.Equal("cred-1", Assert.Single(original!.Definition.GetLookups()).CredentialId);
    }
}
