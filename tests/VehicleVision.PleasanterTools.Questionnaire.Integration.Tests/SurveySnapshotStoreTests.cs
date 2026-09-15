using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>公開した版を 3 RDBMS へ保存して読み戻せることを確かめる。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </remarks>
public class SurveySnapshotStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static SurveyDefinition Definition(int version) => new()
    {
        SurveyId = "s1",
        Version = version,
        Title = LocalizedText.Japanese("顧客満足度アンケート🙏"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "q1",
                        Type = QuestionType.Radio,
                        Title = LocalizedText.Japanese("ご満足いただけましたか"),
                        IsRequired = true,
                        Choices = [new Choice("5", LocalizedText.Japanese("とても満足"))],
                    },
                ],
            },
        ],
    };

    private static MappingDefinition Mapping() => new()
    {
        Assignments =
        [
            ColumnAssignment.Converted(
                "NumA",
                MappingConverter.Of(ConverterOperations.Map, ("map.5", "5")),
                new MappingSource("q1")),
        ],
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開した版を読み戻せる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var repository = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";

        await repository.SaveAsync(new SurveyRecord(
            surveyId, publicId, "顧客満足度アンケート", 12345, "DescriptionA", 0, null));

        await repository.PublishAsync(surveyId, 1, Definition(1), Mapping(), null);

        var snapshot = await snapshots.FindAsync(surveyId, 1);

        Assert.NotNull(snapshot);
        Assert.Equal(12345, snapshot.PleasanterSiteId);
        Assert.Equal("DescriptionA", snapshot.ResponseJsonColumn);
        // **絵文字を含む多言語の文字列が壊れずに往復すること**
        Assert.Equal("顧客満足度アンケート🙏", snapshot.Definition.Title.Get("ja"));
        Assert.Equal(QuestionType.Radio, snapshot.Definition.FindQuestion("q1")!.Type);
        Assert.Equal("NumA", snapshot.Mapping.Assignments.Single().TargetColumn);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じ版を上書きしようとすると拒否する(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var repository = new SurveyRepository(factory);

        var surveyId = Guid.NewGuid();
        await repository.SaveAsync(new SurveyRecord(
            surveyId, $"pub-{Guid.NewGuid():N}", "検証用", 1, null, 0, null));
        await repository.PublishAsync(surveyId, 1, Definition(1), Mapping(), null);

        // **版は不変。** 過去の回答を解釈するために要る
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.PublishAsync(surveyId, 1, Definition(1), Mapping(), null));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 公開用IDからアンケートを引ける(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var repository = new SurveyRepository(new DbConnectionFactory(provider, connectionString));

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await repository.SaveAsync(new SurveyRecord(
            surveyId, publicId, "検証用", 99, null, 1, 3));

        var found = await repository.FindByPublicIdAsync(publicId);

        Assert.NotNull(found);
        Assert.Equal(surveyId, found.SurveyId);
        Assert.Equal(99, found.PleasanterSiteId);
        Assert.Equal(3, found.PublishedVersion);
        Assert.Null(found.ResponseJsonColumn);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 存在しない版はnullを返す(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var snapshots = new SurveySnapshotStore(new DbConnectionFactory(provider, connectionString));

        Assert.Null(await snapshots.FindAsync(Guid.NewGuid(), 1));
    }
}
