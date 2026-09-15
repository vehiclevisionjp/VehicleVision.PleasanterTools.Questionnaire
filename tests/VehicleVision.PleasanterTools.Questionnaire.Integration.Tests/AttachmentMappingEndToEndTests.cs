using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>添付が本当に Pleasanter へ届くことを実機で確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile postgres up -d --wait</c>。
/// </para>
/// <para>
/// **組み立てた JSON を見るだけでは足りない。** 対象の列がサイト設定に無いと
/// **エラーにならず静かに無視される**（<c>_documents/実機検証結果.md</c> 6 章）ので、
/// 実際に送って読み直すまで届いたことにならない。
/// </para>
/// </remarks>
public class AttachmentMappingEndToEndTests
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

    /// <summary>添付列を持つサイトを作る。</summary>
    /// <remarks>
    /// **<c>AttachmentsA</c> をサイト設定に入れること。**
    /// 入れないと送っても静かに捨てられ、原因が分からなくなる。
    /// </remarks>
    private static async Task<long> CreateSiteAsync(HttpClient http, string title)
    {
        var body = new
        {
            ApiKey,
            Title = title,
            ReferenceType = "Results",
            SiteSettings = new
            {
                Columns = new object[]
                {
                    new { ColumnName = "ClassA", LabelText = "満足度" },
                    new { ColumnName = "DescriptionA", LabelText = "回答JSON" },
                    new { ColumnName = "AttachmentsA", LabelText = "資料" },
                },
                EditorColumnHash = new Dictionary<string, string[]>
                {
                    ["General"] = ["ClassA", "DescriptionA", "AttachmentsA"],
                },
            },
        };

        using var response = await http.PostAsJsonAsync($"{PleasanterBaseUrl}/api/items/0/createsite", body);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json?["Id"]?.GetValue<long>()
            ?? throw new InvalidOperationException("サイトを作れなかった");
    }

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("添付の割り当て"),
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
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                    new Question
                    {
                        QuestionId = "qf",
                        Type = QuestionType.File,
                        Title = LocalizedText.Japanese("資料"),
                    },
                ],
            },
        ],
    };

    /// <summary>添付の割り当ては <c>1 : 0 : 1</c>。**変換は掛けない。**</summary>
    private static MappingDefinition Mapping() => new()
    {
        Assignments =
        [
            ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
            new ColumnAssignment
            {
                TargetColumn = "AttachmentsA",
                Sources = [new MappingSource("qf", QuestionPort.Files)],
                Converter = null,
            },
        ],
    };

    private static AnsweredAttachment File(string name, string content) =>
        new("qf", new IncomingAttachment(name, Encoding.UTF8.GetBytes(content)));

    /// <summary>拡張子と先頭バイトだけ見る検査器。</summary>
    /// <remarks>
    /// **ウイルススキャンはここでは使わない**（clamd を要求するとこの試験が落ちる）。
    /// スキャナ側は <c>AttachmentScanEndToEndTests</c> で実機確認している。
    /// **検査器を渡さないと添付は素通しではなく拒否される**ので、必ず渡すこと。
    /// </remarks>
    private static AttachmentInspector Inspector() => new(
        AttachmentPolicy.Create([".txt", ".png"], maxFileSizeBytes: 1024 * 1024, maxFileCount: 5));

    [Fact]
    public async Task 添付がPleasanterの添付列へ届く()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreateSiteAsync(http, $"添付 {Guid.NewGuid():N}");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);
        var outbox = new ResponseOutbox(factory);
        var tokens = new ResponseTokenStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "添付の割り当て", siteId, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(surveys, snapshots, outbox, tokens, Inspector());
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

        // --- 添付つきで受け付けて送る ---
        var accepted = await intake.SubmitAsync(
            publicId,
            token,
            [Answer.Of("q1", "満足"), new Answer("qf", []) { FileNames = ["shiryo.txt"] }],
            [File("shiryo.txt", "これは資料です")]);
        Assert.True(
            accepted.Accepted,
            $"受け付けられなかった: {accepted.Rejection} / "
            + $"{string.Join(",", accepted.Errors.IsDefault ? [] : accepted.Errors.Select(e => e.ToString()))} / "
            + $"{string.Join(",", accepted.Attachments.IsDefault ? [] : accepted.Attachments.Select(a => a.ToString()))}");

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var found = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = found.Body?["Response"]?["Data"]?.AsArray()?.SingleOrDefault();
        Assert.NotNull(row);

        // **Get で返るのは Guid / Name / Size のみ。** 内容は含まれない
        var files = row["AttachmentsHash"]?["AttachmentsA"]?.AsArray();
        Assert.NotNull(files);
        var file = Assert.Single(files);
        Assert.Equal("shiryo.txt", file!["Name"]?.GetValue<string>());
        Assert.True(file["Size"]?.GetValue<long>() > 0);

        // **正本の列に Base64 を載せない**
        var responseJson = row["DescriptionHash"]?["DescriptionA"]?.GetValue<string>();
        Assert.NotNull(responseJson);
        Assert.Contains("shiryo.txt", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("これは資料です")),
            responseJson,
            StringComparison.Ordinal);
    }

    /// <summary>添付を外して送り直すと、前の添付が消える。</summary>
    /// <remarks>
    /// **足すだけでは消えない。** 空配列を送っても何も起きないので、
    /// <c>Guid</c> を添えた削除の指定を送っている
    /// （<c>_documents/実機検証結果.md</c> 8 章）。
    /// </remarks>
    [Fact]
    public async Task 添付を外して送り直すと前の添付が消える()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreateSiteAsync(http, $"添付編集 {Guid.NewGuid():N}");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);
        var outbox = new ResponseOutbox(factory);
        var tokens = new ResponseTokenStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "添付編集", siteId, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(surveys, snapshots, outbox, tokens, Inspector());
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

        await intake.SubmitAsync(
            publicId,
            token,
            [Answer.Of("q1", "満足"), new Answer("qf", []) { FileNames = ["old.txt"] }],
            [File("old.txt", "古い資料")]);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        // --- 添付を外して編集する ---
        await intake.SubmitAsync(publicId, token, [Answer.Of("q1", "ふつう")]);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var after = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = after.Body?["Response"]?["Data"]?.AsArray()?.SingleOrDefault();
        Assert.NotNull(row);

        // 値の側はちゃんと更新される
        Assert.Equal("ふつう", row["ClassHash"]?["ClassA"]?.GetValue<string>());

        // **前の添付は消えている**
        var files = row["AttachmentsHash"]?["AttachmentsA"]?.AsArray();
        Assert.Empty(files ?? []);
    }

    [Fact]
    public async Task 添付を差し替えると新しいものだけが残る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var siteId = await CreateSiteAsync(http, $"添付差替 {Guid.NewGuid():N}");

        DatabaseMigrator.MigrateUp(DatabaseProvider.PostgreSql, ConnectionString);
        var factory = new DbConnectionFactory(DatabaseProvider.PostgreSql, ConnectionString);

        var surveys = new SurveyRepository(factory);
        var snapshots = new SurveySnapshotStore(factory);
        var outbox = new ResponseOutbox(factory);
        var tokens = new ResponseTokenStore(factory);

        var surveyId = Guid.NewGuid();
        var publicId = $"pub-{Guid.NewGuid():N}";
        await surveys.SaveAsync(new SurveyRecord(
            surveyId, publicId, "添付差替", siteId, "DescriptionA",
            (int)SurveyStatus.Published, null));
        await surveys.PublishAsync(surveyId, 1, Definition(), Mapping(), null);

        var intake = new ResponseIntake(surveys, snapshots, outbox, tokens, Inspector());
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

        await intake.SubmitAsync(
            publicId,
            token,
            [Answer.Of("q1", "満足"), new Answer("qf", []) { FileNames = ["old.txt"] }],
            [File("old.txt", "古い資料")]);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        // --- 別のファイルへ差し替える ---
        await intake.SubmitAsync(
            publicId,
            token,
            [Answer.Of("q1", "満足"), new Answer("qf", []) { FileNames = ["new.txt"] }],
            [File("new.txt", "新しい資料")]);
        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var after = await pleasanter.FindByResponseTokenAsync(siteId, "DescriptionA", token);
        var row = after.Body?["Response"]?["Data"]?.AsArray()?.SingleOrDefault();
        Assert.NotNull(row);

        // **古いものは残さない。** 送り直しは「足す」ではなく「置き換える」
        var file = Assert.Single(row["AttachmentsHash"]?["AttachmentsA"]?.AsArray()!);
        Assert.Equal("new.txt", file!["Name"]?.GetValue<string>());
    }
}
