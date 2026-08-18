using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>編集中の定義の読み書きを 3 RDBMS で確かめる。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class SurveyDraftStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static (ISurveyDraftStore Drafts, ISurveyRepository Surveys) Create(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
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

    private static SurveyDefinition Definition(Guid surveyId, params string[] questionIds) => new()
    {
        SurveyId = surveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("満足度調査"),
        Description = LocalizedText.Japanese("ご協力ください"),
        ConfirmationMessage = LocalizedText.Japanese("ありがとうございました"),
        Pages =
        [
            new Page
            {
                PageId = "page-1",
                Title = LocalizedText.Japanese("1 ページ目"),
                Questions = questionIds
                    .Select(id => new Question
                    {
                        QuestionId = id,
                        Type = QuestionType.Radio,
                        Title = LocalizedText.Japanese($"設問 {id}"),
                        IsRequired = true,
                        Choices =
                        [
                            new Choice("good", LocalizedText.Japanese("よい")),
                            new Choice("bad", LocalizedText.Japanese("わるい")),
                            new Choice("other", LocalizedText.Japanese("その他"), IsOther: true),
                        ],
                        Settings = new QuestionSettings { MaxLength = 100 },
                    })
                    .ToImmutableArray(),
            },
        ],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 保存して読み直すと同じ定義になる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var definition = Definition(surveyId, "q1", "q2");
        var mapping = new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
                ColumnAssignment.Converted(
                    "ClassB",
                    MappingConverter.Of("join", ("separator", "、")),
                    new MappingSource("q1"),
                    new MappingSource("q2", QuestionPort.OtherText)),
            ],
        };

        var revision = await drafts.SaveAsync(surveyId, definition, mapping, expectedRevision: 0);
        Assert.Equal(1, revision);

        var loaded = await drafts.LoadAsync(surveyId);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Revision);

        Assert.Equal("満足度調査", loaded.Definition.Title.Get("ja"));
        Assert.Equal("ありがとうございました", loaded.Definition.ConfirmationMessage!.Get("ja"));

        var page = Assert.Single(loaded.Definition.Pages);
        Assert.Equal("1 ページ目", page.Title!.Get("ja"));
        Assert.Equal(["q1", "q2"], page.Questions.Select(q => q.QuestionId).ToArray());

        var first = page.Questions[0];
        Assert.Equal(QuestionType.Radio, first.Type);
        Assert.True(first.IsRequired);
        Assert.Equal(100, first.Settings.MaxLength);
        // **「その他」の印が残ること。** 落ちると自由記述欄が出なくなる
        Assert.Equal(["good", "bad", "other"], first.Choices.Select(c => c.Value).ToArray());
        Assert.True(first.Choices[2].IsOther);

        Assert.Equal(2, loaded.Mapping.Assignments.Length);
        var direct = loaded.Mapping.Assignments.Single(a => a.TargetColumn == "ClassA");
        Assert.Null(direct.Converter);
        Assert.Equal("q1", Assert.Single(direct.Sources).QuestionId);

        var converted = loaded.Mapping.Assignments.Single(a => a.TargetColumn == "ClassB");
        Assert.Equal("join", converted.Converter!.Operation);
        Assert.Equal("、", converted.Converter.Config["separator"]);
        // **入力の順序が変換の結果を決める。** 崩れてはいけない
        Assert.Equal(["q1", "q2"], converted.Sources.Select(s => s.QuestionId).ToArray());
        Assert.Equal(QuestionPort.OtherText, converted.Sources[1].Port);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 消した設問と割り当ては残らない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var revision = await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, "q1", "q2"),
            new MappingDefinition
            {
                Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q2"))],
            },
            expectedRevision: 0);

        // q2 を消し、割り当ても消す
        await drafts.SaveAsync(
            surveyId,
            Definition(surveyId, "q1"),
            new MappingDefinition { Assignments = [] },
            expectedRevision: revision);

        var loaded = await drafts.LoadAsync(surveyId);

        // **入れ替える。** 残ると、消したはずの設問へ回答が付く
        Assert.Equal(["q1"], loaded!.Definition.Pages[0].Questions.Select(q => q.QuestionId).ToArray());
        Assert.Empty(loaded.Mapping.Assignments);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 古い版で保存しようとすると弾かれる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        await drafts.SaveAsync(
            surveyId, Definition(surveyId, "q1"), new MappingDefinition(), expectedRevision: 0);

        // **同じ版で 2 回目を投げる。** 先に読んだ画面から保存した状況
        var conflict = await Assert.ThrowsAsync<SurveyDraftConflictException>(() =>
            drafts.SaveAsync(
                surveyId, Definition(surveyId, "q2"), new MappingDefinition(), expectedRevision: 0));

        Assert.Equal(0, conflict.Expected);
        Assert.Equal(1, conflict.Actual);

        // **弾かれた側の変更は入っていない**
        var loaded = await drafts.LoadAsync(surveyId);
        Assert.Equal(["q1"], loaded!.Definition.Pages[0].Questions.Select(q => q.QuestionId).ToArray());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 次に公開される版が入っている(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var loaded = await drafts.LoadAsync(surveyId);
        // まだ公開していないので次は 1 版目
        Assert.Equal(1, loaded!.Definition.Version);

        await surveys.PublishAsync(
            surveyId, 1, loaded.Definition, loaded.Mapping, publishedBy: null);

        var afterPublish = await drafts.LoadAsync(surveyId);
        Assert.Equal(2, afterPublish!.Definition.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一覧に出る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, surveys) = Create(provider, connectionString);
        var surveyId = await CreateSurveyAsync(surveys);

        var list = await drafts.ListAsync();

        Assert.Contains(list, summary => summary.SurveyId == surveyId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 無いアンケートはnullが返る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var (drafts, _) = Create(provider, connectionString);

        Assert.Null(await drafts.LoadAsync(Guid.NewGuid()));
    }
}
