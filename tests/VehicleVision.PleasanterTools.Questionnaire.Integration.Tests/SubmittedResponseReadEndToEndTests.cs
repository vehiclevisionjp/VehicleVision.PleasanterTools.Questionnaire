using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// 送信済みの回答の読み直し（Issue #908）を、実アプリ（SQLite）と偽の Pleasanter で通しで確かめる。
/// <b>実機の Pleasanter は使わない。</b>
/// </summary>
public sealed class SubmittedResponseReadEndToEndTests : IAsyncLifetime
{
    private const long SiteId = 4242;

    private readonly string directory = Path.Combine(
        FindRepositoryRoot(), "temp", "20261106_read-submitted", Guid.NewGuid().ToString("N"));
    private readonly FakePleasanter pleasanter = new();
    private ApiFactory factory = null!;
    private string publicId = string.Empty;
    private Guid surveyId;

    /// <summary>レコードを覚えるだけの偽の Pleasanter。</summary>
    private sealed class FakePleasanter : HttpMessageHandler
    {
        public Dictionary<long, JsonObject> Records { get; } = [];

        public int GetCount { get; private set; }

        public Func<HttpStatusCode?> Fail { get; set; } = () => null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            var id = long.Parse(segments[^2]);
            var action = segments[^1];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();

            if (action == "Get")
            {
                GetCount++;
                if (Fail() is { } status)
                {
                    return Json(status, new JsonObject { ["StatusCode"] = (int)status });
                }

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

        await SeedAsync(responseJsonColumn: "DescriptionA", allowEditing: true);
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

    private async Task<string> SeedAsync(string? responseJsonColumn, bool allowEditing)
    {
        var repository = factory.Services.GetRequiredService<ISurveyRepository>();
        var id = Guid.NewGuid();
        var pub = "pub" + Guid.NewGuid().ToString("N")[..12];
        await repository.SaveAsync(new SurveyRecord(
            id, pub, "再編集", SiteId, responseJsonColumn, (int)SurveyStatus.Published, null));
        await repository.PublishAsync(
            id,
            1,
            new SurveyDefinition
            {
                SurveyId = id.ToString(),
                Version = 1,
                Title = LocalizedText.Japanese("再編集"),
                AllowEditingAfterSubmit = allowEditing,
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
        return pub;
    }

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private static string NewToken() => $"tok-{Guid.NewGuid():N}";

    private async Task SubmitAndSendAsync(string token, string value)
    {
        var intake = factory.Services.GetRequiredService<ResponseIntake>();
        var accepted = await intake.SubmitAsync(publicId, token, [Answer.Of("q1", value)]);
        Assert.True(accepted.Accepted);
    }

    private async Task SendAsync() =>
        Assert.Equal(SendOutcome.Sent, await factory.Services.GetRequiredService<ResponseSender>().SendOnceAsync());

    private static async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> GetAsync(
        HttpClient client, string id, string token)
    {
        var response = await client.GetAsync($"/api/forms/{id}/responses/{token}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static string FirstValue(string text) =>
        JsonDocument.Parse(text).RootElement.GetProperty("answers")[0].GetProperty("values")[0].GetString()!;

    [Fact]
    public async Task 送信待ちにあるうちは送信待ちを返し_Pleasanterを見ない()
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "未送信");

        var (status, text, response) = await GetAsync(client, publicId, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("未送信", FirstValue(text));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(0, pleasanter.GetCount);
    }

    [Fact]
    public async Task 送信を終えて送信待ちが空になっても_Pleasanterから読める()
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "太郎");
        await SendAsync();
        var intake = factory.Services.GetRequiredService<ResponseIntake>();
        Assert.Null(await intake.FindPendingAsync(token));

        var (status, text, response) = await GetAsync(client, publicId, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("太郎", FirstValue(text));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);

        // 内部の事情を返さない（トークン・レコード ID・サイト ID・列名）
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("DescriptionA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultId", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SiteId.ToString(), text, StringComparison.Ordinal);

        // 編集して送ると、同じレコードが更新され、読み直しにも反映される
        await SubmitAndSendAsync(token, "花子");
        await SendAsync();
        Assert.Single(pleasanter.Records);
        Assert.Equal("花子", FirstValue((await GetAsync(client, publicId, token)).Text));
    }

    [Fact]
    public async Task 他のトークンと他のアンケートからは読めない()
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "太郎");
        await SendAsync();
        var other = NewToken();
        await SubmitAndSendAsync(other, "次郎");
        await SendAsync();

        var getsBefore = pleasanter.GetCount;

        // 知らないトークンは Pleasanter へ問い合わせない
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, publicId, NewToken())).Status);
        Assert.Equal(getsBefore, pleasanter.GetCount);

        // 別のアンケートの公開 ID では読めない（送信済みも送信待ちも）
        var firstPublicId = publicId;
        var otherPublicId = await SeedAsync("DescriptionA", allowEditing: true);
        Assert.NotEqual(firstPublicId, otherPublicId);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, otherPublicId, token)).Status);
        var pendingToken = NewToken();
        publicId = firstPublicId;
        await SubmitAndSendAsync(pendingToken, "未送信");
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, otherPublicId, pendingToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, firstPublicId, pendingToken)).Status);

        // 取り違えたレコード（JSON のトークンが違う）は返さない
        var map = factory.Services.GetRequiredService<IResponseTokenStore>();
        var first = (await map.FindAsync(token))!.ReferenceId!.Value;
        var second = (await map.FindAsync(other))!.ReferenceId!.Value;
        await map.SaveAsync(token, (await map.FindAsync(token))!.SurveyId, second);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, firstPublicId, token)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Pleasanterが失敗したら_読めない旨の503を返し_内部の情報を出さない(HttpStatusCode failure)
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "太郎");
        await SendAsync();
        pleasanter.Fail = () => failure;

        var (status, text, _) = await GetAsync(client, publicId, token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task レコードが消されていたら404()
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "太郎");
        await SendAsync();
        pleasanter.Records.Clear();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, publicId, token)).Status);
    }

    [Fact]
    public async Task 回答JSON列が無い_または編集を許さないアンケートはPleasanterを見ずに404()
    {
        using var client = Client();

        foreach (var (column, allow) in new (string?, bool)[] { (null, true), ("DescriptionA", false) })
        {
            await SeedAsync(column, allow);
            var token = NewToken();
            await SubmitAndSendAsync(token, "太郎");
            await SendAsync();
            var before = pleasanter.GetCount;

            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(client, publicId, token)).Status);
            Assert.Equal(before, pleasanter.GetCount);
        }
    }

    [Fact]
    public async Task 添付はファイル名だけを返し_中身は返さない()
    {
        using var client = Client();
        var token = NewToken();
        await SubmitAndSendAsync(token, "太郎");
        await SendAsync();
        var referenceId = (await factory.Services.GetRequiredService<IResponseTokenStore>().FindAsync(token))!.ReferenceId!.Value;
        var payload = new ResponsePayload(token,
        [
            new PayloadAnswer("q1", ["太郎"], null, ["memo.txt"])
            {
                Files = [new PayloadFile("memo.txt", "SECRET-BASE64")],
            },
        ]);
        pleasanter.Records[referenceId]["DescriptionHash"] = new JsonObject
        {
            ["DescriptionA"] = payload.ToJson(),
        };

        var (status, text, _) = await GetAsync(client, publicId, token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("memo.txt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-BASE64", text, StringComparison.Ordinal);
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
                DataSource = Path.Combine(directory, "read.db"),
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
