using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// テスト回答の削除（Issue #1010）を、実アプリ（SQLite）と偽の Pleasanter で通しで確かめる。
/// <b>実機の Pleasanter は使わない。</b>削除 API の実機の挙動は未検証。
/// </summary>
public sealed class TestResponseCleanupEndToEndTests : IAsyncLifetime
{
    private const long SiteId = 4242;

    private readonly string directory = Path.Combine(
        FindRepositoryRoot(), "temp", "20261118_test-response-cleanup", Guid.NewGuid().ToString("N"));
    private readonly FakePleasanter pleasanter = new();
    private ApiFactory factory = null!;
    private string publicId = string.Empty;
    private Guid surveyId;

    /// <summary>レコードを覚え、削除の呼び出しを数える偽の Pleasanter。</summary>
    private sealed class FakePleasanter : HttpMessageHandler
    {
        public Dictionary<long, JsonObject> Records { get; } = [];

        public List<long> Deleted { get; } = [];

        public HttpStatusCode? DeleteFails { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var id = long.Parse(segments[^2]);
            var action = segments[^1];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();

            if (action == "Get")
            {
                var data = new JsonArray();
                if (Records.TryGetValue(id, out var row))
                {
                    data.Add(row.DeepClone());
                }

                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["StatusCode"] = 200,
                    ["Response"] = new JsonObject { ["Data"] = data },
                });
            }

            if (action == "Delete")
            {
                if (DeleteFails is { } status)
                {
                    return Json(status, new JsonObject { ["StatusCode"] = (int)status });
                }

                Deleted.Add(id);
                Records.Remove(id);
                return Json(HttpStatusCode.OK, new JsonObject { ["StatusCode"] = 200, ["Id"] = id });
            }

            var record = action == "Create"
                ? new JsonObject { ["ResultId"] = id = Records.Count + 100, ["SiteId"] = SiteId }
                : Records[id];
            if (body["DescriptionHash"] is JsonObject hash)
            {
                record["DescriptionHash"] = hash.DeepClone();
            }

            Records[id] = record;
            return Json(HttpStatusCode.OK, new JsonObject { ["StatusCode"] = 200, ["Id"] = id });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }

    public async Task InitializeAsync()
    {
        factory = new ApiFactory(directory, pleasanter);
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, factory.ConnectionString);
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            using var ready = await client.GetAsync("/ready", timeout.Token);
            if (ready.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }

        await SeedAsync(responseJsonColumn: "DescriptionA");
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task SeedAsync(string? responseJsonColumn)
    {
        var repository = factory.Services.GetRequiredService<ISurveyRepository>();
        var id = Guid.NewGuid();
        var pub = "pub" + Guid.NewGuid().ToString("N")[..12];
        await repository.SaveAsync(new SurveyRecord(
            id, pub, "テスト削除", SiteId, responseJsonColumn, (int)SurveyStatus.TestPublished, null));
        await repository.PublishAsync(
            id,
            1,
            new SurveyDefinition
            {
                SurveyId = id.ToString(),
                Version = 1,
                Title = LocalizedText.Japanese("テスト削除"),
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
                                Title = LocalizedText.Japanese("お名前"),
                            },
                        ],
                    },
                ],
            },
            new MappingDefinition(),
            null);
        publicId = pub;
        surveyId = id;
    }

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    private TestResponseCleaner Cleaner => factory.Services.GetRequiredService<TestResponseCleaner>();

    private IResponseTokenStore Tokens => factory.Services.GetRequiredService<IResponseTokenStore>();

    /// <summary>回答を受け付けて Pleasanter へ送る（<paramref name="send"/> が偽なら送信待ちのまま）。</summary>
    private async Task<string> SubmitAsync(bool send = true)
    {
        var token = NewToken();
        var accepted = await factory.Services.GetRequiredService<ResponseIntake>()
            .SubmitAsync(publicId, token, [Answer.Of("q1", "太郎")]);
        Assert.True(accepted.Accepted);
        if (send)
        {
            Assert.Equal(
                SendOutcome.Sent,
                await factory.Services.GetRequiredService<ResponseSender>().SendOnceAsync());
        }

        return token;
    }

    /// <summary>テスト公開から本公開へ切り替える（以降の回答は本番扱い）。</summary>
    private async Task GoLiveAsync()
    {
        await using var connection = new SqliteConnection(factory.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE Surveys SET Status = @Status WHERE SurveyId = @SurveyId",
            new { Status = (int)SurveyStatus.Published, SurveyId = surveyId });
    }

    [Fact]
    public async Task テスト回答だけが消え_本番の回答は残る()
    {
        var test1 = await SubmitAsync();
        var test2 = await SubmitAsync();
        await GoLiveAsync();
        var live = await SubmitAsync();
        var liveId = (await Tokens.FindAsync(live))!.ReferenceId!.Value;

        Assert.Equal(2, await Cleaner.CountAsync(surveyId));

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 2);

        Assert.Equal(TestResponseCleanupStatus.Completed, result.Status);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Remaining);
        Assert.Equal(2, pleasanter.Deleted.Count);
        Assert.DoesNotContain(liveId, pleasanter.Deleted);
        Assert.True(pleasanter.Records.ContainsKey(liveId));
        Assert.Null(await Tokens.FindAsync(test1));
        Assert.Null(await Tokens.FindAsync(test2));
        Assert.NotNull(await Tokens.FindAsync(live));
        Assert.Equal(0, await Cleaner.CountAsync(surveyId));
    }

    [Fact]
    public async Task 画面の件数と今の件数が違えば_何も消さない()
    {
        await SubmitAsync();
        await SubmitAsync();

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 1);

        Assert.Equal(TestResponseCleanupStatus.CountChanged, result.Status);
        Assert.Equal(2, result.Current);
        Assert.Empty(pleasanter.Deleted);
        Assert.Equal(2, pleasanter.Records.Count);
    }

    [Fact]
    public async Task 送信待ちのテスト回答は対象にしない()
    {
        await SubmitAsync(send: false);

        Assert.Equal(0, await Cleaner.CountAsync(surveyId));
        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 0);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(pleasanter.Deleted);
    }

    [Theory]
    [InlineData("site")]
    [InlineData("id")]
    [InlineData("token")]
    public async Task 照合が合わないレコードは_消さずに触らなかった件数へ数える(string mismatch)
    {
        var token = await SubmitAsync();
        var referenceId = (await Tokens.FindAsync(token))!.ReferenceId!.Value;
        var record = pleasanter.Records[referenceId];
        switch (mismatch)
        {
            case "site":
                record["SiteId"] = SiteId + 1;
                break;
            case "id":
                record["ResultId"] = referenceId + 1;
                break;
            default:
                record["DescriptionHash"] = new JsonObject
                {
                    ["DescriptionA"] = new ResponsePayload("tok-other", []).ToJson(),
                };
                break;
        }

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 1);

        Assert.Equal(TestResponseCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(pleasanter.Deleted);
        Assert.NotNull(await Tokens.FindAsync(token));
    }

    [Fact]
    public async Task 回答JSON列が無ければ_持ち主を確かめられないので何も消さない()
    {
        await SeedAsync(responseJsonColumn: null);
        await SubmitAsync(send: false);

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 0);

        Assert.Equal(TestResponseCleanupStatus.NotVerifiable, result.Status);
        Assert.Empty(pleasanter.Deleted);
    }

    [Fact]
    public async Task Pleasanterに既に無ければ_対応表だけ片付ける()
    {
        var token = await SubmitAsync();
        pleasanter.Records.Clear();

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 1);

        Assert.Equal(1, result.AlreadyGone);
        Assert.Equal(0, result.Deleted);
        Assert.Empty(pleasanter.Deleted);
        Assert.Null(await Tokens.FindAsync(token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task 削除を断られたら_残りを試さずに止め_対応表を残す(HttpStatusCode refusal)
    {
        var first = await SubmitAsync();
        var second = await SubmitAsync();
        pleasanter.DeleteFails = refusal;

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 2);

        Assert.Equal(TestResponseCleanupStatus.Forbidden, result.Status);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(2, pleasanter.Records.Count);
        Assert.NotNull(await Tokens.FindAsync(first));
        Assert.NotNull(await Tokens.FindAsync(second));
        Assert.Equal(2, result.Remaining);
    }

    [Fact]
    public async Task 一時的な失敗は失敗の件数へ数え_対応表を残して再実行できる()
    {
        var token = await SubmitAsync();
        pleasanter.DeleteFails = HttpStatusCode.ServiceUnavailable;

        var result = await Cleaner.DeleteAsync(surveyId, expectedCount: 1);

        Assert.Equal(TestResponseCleanupStatus.Completed, result.Status);
        Assert.Equal(1, result.Failed);
        Assert.NotNull(await Tokens.FindAsync(token));

        pleasanter.DeleteFails = null;
        var retry = await Cleaner.DeleteAsync(surveyId, expectedCount: 1);
        Assert.Equal(1, retry.Deleted);
        Assert.Null(await Tokens.FindAsync(token));
    }

    [Fact]
    public async Task 対応表の削除は_本番の行を消さない()
    {
        await GoLiveAsync();
        var live = await SubmitAsync();

        Assert.False(await Tokens.DeleteTestAsync(live, surveyId));
        Assert.NotNull(await Tokens.FindAsync(live));
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "VehicleVision.PleasanterTools.Questionnaire.slnx")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Questionnaire のソリューションが見つかりません。");
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string directory;
        private readonly FakePleasanter fake;

        public ApiFactory(string directory, FakePleasanter fake)
        {
            this.directory = directory;
            this.fake = fake;
            Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "cleanup.db"),
                Pooling = false,
            }.ToString();
            var parameters = Path.Combine(directory, "App_Data", "Parameters");
            Directory.CreateDirectory(parameters);
            File.WriteAllText(
                Path.Combine(parameters, "General.local.json"),
                JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["QUESTIONNAIRE_DB_PROVIDER"] = "Sqlite",
                    ["QUESTIONNAIRE_DB_CONNECTIONSTRING"] = ConnectionString,
                    ["QUESTIONNAIRE_DB_AUTO_MIGRATE"] = "false",
                    ["QUESTIONNAIRE_DB_SKIP_MIGRATION_CHECK"] = "false",
                    ["QUESTIONNAIRE_SECRET_KEY"] = SecretProtector.GenerateKey(),
                    ["QUESTIONNAIRE_ADMIN_SESSION_STORE"] = "Database",
                    ["QUESTIONNAIRE_SAML_ENABLED"] = "false",
                    ["QUESTIONNAIRE_PLEASANTERSSO_ENABLED"] = "false",
                    ["QUESTIONNAIRE_REQUESTS_PER_MIN"] = "100000",
                    ["QUESTIONNAIRE_FORM_REQUESTS_PER_MIN"] = "100000",
                    ["QUESTIONNAIRE_ASSET_PATH"] = Path.Combine(directory, "assets"),
                    ["QUESTIONNAIRE_PLEASANTER_BASEURL"] = "http://localhost:1",
                    ["QUESTIONNAIRE_PLEASANTER_APIKEY"] = "contract-test-not-a-real-key",
                }));
        }

        public string ConnectionString { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(directory);
            builder.ConfigureTestServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                Type[] deliveryWorkers =
                [
                    typeof(ResponseSenderHostedService),
                    typeof(AssetHistorySenderHostedService),
                    typeof(MailSenderHostedService),
                    typeof(ResponseNotificationMailerHostedService),
                ];
                foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } type && deliveryWorkers.Contains(type)).ToArray())
                {
                    services.Remove(service);
                }

                // 偽の Pleasanter へ向ける（送信ワーカーも読み出しも同じ口を使う）
                services.RemoveAll<PleasanterGateway>();
                services.AddTransient(_ => new PleasanterGateway(
                    new HttpClient(fake, disposeHandler: false),
                    new PleasanterOptions
                    {
                        BaseUrl = "http://localhost:1",
                        ApiKey = "fake",
                        ApiKeyUserTimeZoneId = "Asia/Tokyo",
                    }));
            });
        }
    }
}
