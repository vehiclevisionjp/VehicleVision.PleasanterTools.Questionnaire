using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>受付から Pleasanter へ届くまでを端から端まで確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// 起動は <c>docker compose --profile postgres up -d --wait</c>。
/// </para>
/// </remarks>
public class EndToEndTests
{
    private const string ApiKey =
        "6ce6c0fd2f4aea3c093c3ebdd7d4ea3250a132b4867d68e7d1abe35c2499664abb398d74603c2b2a38e31a21319955b3c43ede30394ead7be37ddd615c34e1f6";

    private const string ConnectionString =
        "Host=localhost;Port=15432;Database=questionnaire;Username=postgres;Password=Questionnaire#Test1";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string PleasanterBaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_PLEASANTER_BASEURL")
            ?? "http://localhost:8080";

    /// <param name="extraColumns">
    /// 追加で用意する列。**用意していない列へ書いても Pleasanter は黙って捨てる**
    /// ので、行ごとに列を分ける検証（Issue #74）ではここに足すこと。
    /// </param>
    private static async Task<long> CreatePleasanterSiteAsync(
        HttpClient http,
        string title,
        params string[] extraColumns)
    {
        string[] columnNames = ["ClassA", "NumA", "DescriptionA", .. extraColumns];

        var body = new
        {
            ApiKey,
            Title = title,
            ReferenceType = "Results",
            SiteSettings = new
            {
                Columns = columnNames
                    .Select(name => new { ColumnName = name, LabelText = name })
                    .ToArray(),
                EditorColumnHash = new Dictionary<string, string[]>
                {
                    ["General"] = columnNames,
                },
            },
        };

        using var response = await http.PostAsJsonAsync(
            $"{PleasanterBaseUrl}/api/items/0/CreateSite", body);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node?["Id"]?.GetValue<long>()
            ?? throw new InvalidOperationException("検証用サイトを作成できなかった");
    }

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("端から端までの検証"),
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
                        Choices =
                        [
                            new Choice("very", LocalizedText.Japanese("とても満足")),
                            new Choice("ok", LocalizedText.Japanese("満足")),
                        ],
                    },
                ],
            },
        ],
    };

    private static MappingDefinition Mapping() => new()
    {
        Assignments =
        [
            ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
            ColumnAssignment.Converted(
                "NumA",
                MappingConverter.Of(ConverterOperations.Map, ("map.very", "5"), ("map.ok", "4")),
                new MappingSource("q1")),
        ],
    };

    [Fact]
    public async Task 回答を受け付けてPleasanterへ届き編集も反映される()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreatePleasanterSiteAsync(http, $"E2E {Guid.NewGuid():N}");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);
        var outbox = new ResponseOutbox(factory);
        var tokens = new ResponseTokenStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "端から端までの検証", siteId, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(surveys, snapshots, outbox, tokens);
        var pleasanter = new PleasanterApiClient(http, new PleasanterOptions
        {
            BaseUrl = PleasanterBaseUrl,
            ApiKey = ApiKey,
            ApiKeyUserTimeZoneId = "Asia/Tokyo",
        });

        var sender = new ResponseSender(
            outbox,
            tokens,
            snapshots,
            pleasanter,
            new PleasanterRecordBuilder(new PleasanterDateTime("Asia/Tokyo")),
            new MappingEvaluator(),
            new ResponseSenderOptions(),
            NullLogger<ResponseSender>.Instance);

        // --- 公開中の定義を返せる ---
        var (form, rejection) = await intake.GetPublishedAsync(publicId);
        Assert.Null(rejection);
        Assert.Equal("ご満足いただけましたか", form!.Definition.FindQuestion("q1")!.Title.Get("ja"));

        // **既定は proof-of-work を要る**（Issue #66）。移行しただけで守りが緩まない
        Assert.True(form.RequiresProofOfWork);

        // --- 受付 ---
        var token = $"tok-{Guid.NewGuid():N}";
        var accepted = await intake.SubmitAsync(publicId, token, [Answer.Of("q1", "very")]);
        Assert.True(accepted.Accepted);

        // **受付直後は送信待ちにある。** Pleasanter にはまだ無い
        var pending = await intake.FindPendingAsync(token);
        Assert.NotNull(pending);
        Assert.Equal(token, pending.Token);

        // --- 送信 ---
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        // **送れたら送信待ちから消える**
        Assert.Null(await intake.FindPendingAsync(token));

        var referenceId = await tokens.FindReferenceIdAsync(token);
        Assert.NotNull(referenceId);

        // --- Pleasanter 側を確かめる ---
        var found = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = found.Body?["Response"]?["Data"]?.AsArray()?.SingleOrDefault();
        Assert.NotNull(row);
        Assert.Equal("very", row["ClassHash"]?["ClassA"]?.GetValue<string>());
        Assert.Equal(5, row["NumHash"]?["NumA"]?.GetValue<decimal>());

        // --- 編集 ---
        var edited = await intake.SubmitAsync(publicId, token, [Answer.Of("q1", "ok")]);
        Assert.True(edited.Accepted);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var after = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var rows = after.Body?["Response"]?["Data"]?.AsArray();
        Assert.NotNull(rows);

        // **編集は新規作成にならない。同じレコードが更新される**
        var updatedRow = Assert.Single(rows);
        Assert.NotNull(updatedRow);
        Assert.Equal("ok", updatedRow["ClassHash"]?["ClassA"]?.GetValue<string>());
        Assert.Equal(4, updatedRow["NumHash"]?["NumA"]?.GetValue<decimal>());
        Assert.Equal(referenceId, updatedRow["ResultId"]?.GetValue<long>());
    }

    // ---- グリッドとランキング（Issue #74）------------------------------------

    private static SurveyDefinition GridDefinition() => new()
    {
        SurveyId = "s-grid",
        Version = 1,
        Title = LocalizedText.Japanese("グリッドの検証"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "q-grid",
                        Type = QuestionType.Grid,
                        Title = LocalizedText.Japanese("それぞれについて教えてください"),
                        IsRequired = true,
                        Choices =
                        [
                            new Choice("good", LocalizedText.Japanese("良い")),
                            new Choice("bad", LocalizedText.Japanese("悪い")),
                        ],
                        Settings = new QuestionSettings
                        {
                            Rows =
                            [
                                new GridRow("price", LocalizedText.Japanese("価格")),
                                new GridRow("quality", LocalizedText.Japanese("品質")),
                            ],
                        },
                    },
                    new Question
                    {
                        QuestionId = "q-rank",
                        Type = QuestionType.Ranking,
                        Title = LocalizedText.Japanese("大事な順に選んでください"),
                        Choices =
                        [
                            new Choice("speed", LocalizedText.Japanese("速さ")),
                            new Choice("cost", LocalizedText.Japanese("安さ")),
                        ],
                    },
                ],
            },
        ],
    };

    /// <summary>**行ごとに 1 列**。ランキングは順位を数値の列へ。</summary>
    private static MappingDefinition GridMapping() => new()
    {
        Assignments =
        [
            ColumnAssignment.Direct(
                "ClassA", new MappingSource("q-grid", QuestionPort.Value, "price")),
            ColumnAssignment.Direct(
                "ClassB", new MappingSource("q-grid", QuestionPort.Value, "quality")),
            ColumnAssignment.Direct(
                "NumA", new MappingSource("q-rank", QuestionPort.Value, "speed")),
        ],
    };

    [Fact]
    public async Task グリッドとランキングがPleasanterの列まで届く()
    {
        if (!Enabled)
        {
            return;
        }

        // **行ごとの回答は Values を通らない。** 経路のどこかで落とすと、
        // 受け付けたのに列が空のまま Pleasanter へ入る
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreatePleasanterSiteAsync(
            http, $"E2E grid {Guid.NewGuid():N}", "ClassB");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);
        var outbox = new ResponseOutbox(factory);
        var tokens = new ResponseTokenStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "グリッドの検証", siteId, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, GridDefinition(), GridMapping(), null);

        var intake = new ResponseIntake(surveys, snapshots, outbox, tokens);
        var pleasanter = new PleasanterApiClient(http, new PleasanterOptions
        {
            BaseUrl = PleasanterBaseUrl,
            ApiKey = ApiKey,
            ApiKeyUserTimeZoneId = "Asia/Tokyo",
        });

        var sender = new ResponseSender(
            outbox,
            tokens,
            snapshots,
            pleasanter,
            new PleasanterRecordBuilder(new PleasanterDateTime("Asia/Tokyo")),
            new MappingEvaluator(),
            new ResponseSenderOptions(),
            NullLogger<ResponseSender>.Instance);

        var token = $"tok-{Guid.NewGuid():N}";
        var gridAnswer = new Answer("q-grid", [])
        {
            Rows = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
            {
                ["price"] = ["good"],
                ["quality"] = ["bad"],
            }.ToImmutableDictionary(StringComparer.Ordinal),
        };

        // ランキングは並べた順そのものが答え。**speed が 1 位**
        var accepted = await intake.SubmitAsync(
            publicId, token, [gridAnswer, Answer.Of("q-rank", "speed", "cost")]);

        Assert.True(accepted.Accepted);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var found = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = found.Body?["Response"]?["Data"]?.AsArray()?.SingleOrDefault();

        Assert.NotNull(row);
        Assert.Equal("good", row["ClassHash"]?["ClassA"]?.GetValue<string>());
        Assert.Equal("bad", row["ClassHash"]?["ClassB"]?.GetValue<string>());
        Assert.Equal(1, row["NumHash"]?["NumA"]?.GetValue<decimal>());
    }

    [Fact]
    public async Task グリッドは行が全部埋まっていないと受け付けない()
    {
        if (!Enabled)
        {
            return;
        }

        // **行の一部だけ答えて送れると、どこまで答えたのか誰にも分からなくなる**
        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";

        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "グリッドの検証", 0, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, GridDefinition(), GridMapping(), null);

        var intake = new ResponseIntake(
            surveys, new SurveySnapshotStore(factory), new ResponseOutbox(factory),
            new ResponseTokenStore(factory));

        var half = new Answer("q-grid", [])
        {
            Rows = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
            {
                ["price"] = ["good"],
            }.ToImmutableDictionary(StringComparer.Ordinal),
        };

        var result = await intake.SubmitAsync(publicId, $"tok-{Guid.NewGuid():N}", [half]);

        // **「見つからない」で落ちていないこと。** 引数を取り違えると、
        // 検証に届かないまま試験だけが通る
        Assert.Equal(IntakeRejection.Invalid, result.Rejection);
        Assert.Contains(
            result.Errors,
            error => error.Code == ValidationErrorCode.RowRequired && error.Detail == "quality");
    }

    [Fact]
    public async Task 必須未回答は受け付けない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreatePleasanterSiteAsync(http, $"E2E {Guid.NewGuid():N}");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);
        var surveys = new SurveyRepository(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "検証用", siteId, "DescriptionA", (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(
            surveys, new SurveySnapshotStore(factory), new ResponseOutbox(factory),
            new ResponseTokenStore(factory));

        // **サーバ側で必ず検証する**
        var result = await intake.SubmitAsync(publicId, $"tok-{Guid.NewGuid():N}", []);

        Assert.False(result.Accepted);
        Assert.Equal(IntakeRejection.Invalid, result.Rejection);
    }

    [Fact]
    public async Task 受付期間外は理由を返して受け付けない()
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);
        var surveys = new SurveyRepository(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "検証用", 1, null, (int)SurveyStatus.Published, 1,
            AcceptTo: DateTime.UtcNow.AddDays(-1)));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(
            surveys, new SurveySnapshotStore(factory), new ResponseOutbox(factory),
            new ResponseTokenStore(factory));

        var result = await intake.SubmitAsync(publicId, $"tok-{Guid.NewGuid():N}", [Answer.Of("q1", "very")]);

        Assert.Equal(IntakeRejection.Closed, result.Rejection);
    }

    [Fact]
    public async Task 存在しない公開IDと未公開を区別しない()
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);
        var surveys = new SurveyRepository(factory);

        // 未公開のアンケート
        var draftPublicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            Guid.NewGuid(), draftPublicId, "下書き", 1, null, (int)SurveyStatus.Draft, null));

        var intake = new ResponseIntake(
            surveys, new SurveySnapshotStore(factory), new ResponseOutbox(factory),
            new ResponseTokenStore(factory));

        var draft = await intake.GetPublishedAsync(draftPublicId);
        var missing = await intake.GetPublishedAsync($"pub-{Guid.NewGuid():N}");

        // **区別すると、公開 ID の総当たりで「実在するか」が分かってしまう**
        Assert.Equal(IntakeRejection.NotFound, draft.Rejection);
        Assert.Equal(IntakeRejection.NotFound, missing.Rejection);
    }
}
