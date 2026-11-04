using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// ルックアップの公開 API を、実アプリ（SQLite）と偽の検索サービスで通しで確かめる（Issue #832）。
/// <b>実機の Pleasanter は使わない。</b>
/// </summary>
public sealed class LookupPublicApiEndToEndTests : IAsyncLifetime
{
    private const string KeyValue = "KEY-VALUE-9f3a1c";
    private const string FetchedValue = "FETCHED-東京都-7d2b";

    private readonly string directory = Path.Combine(
        FindRepositoryRoot(), "temp", "20261103_lookup-public-api", Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider logs = new();
    private readonly FakeSearch search = new();
    private LookupPublicOptions options = new();
    private ApiFactory factory = null!;
    private string publicId = string.Empty;
    private Guid surveyId;

    private sealed class FakeSearch : ILookupSearchService
    {
        public List<(Guid SurveyId, Dictionary<string, string?> Keys)> Calls { get; } = [];

        public Func<Task<LookupSearchResult>> Behave { get; set; } = () => Task.FromResult(
            new LookupSearchResult
            {
                Status = LookupSearchStatus.Success,
                Candidates =
                [
                    new LookupCandidateRow(new Dictionary<string, string?>
                    {
                        ["ClassB"] = FetchedValue,
                        ["Title"] = "内部の題名",
                    }),
                ],
            });

        public Task<LookupSearchResult> SearchAsync(
            Guid surveyId,
            LookupDefinition lookup,
            IReadOnlyDictionary<string, string?> keyValues,
            CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((surveyId, new Dictionary<string, string?>(keyValues)));
            }

            return Behave();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                {
                    owner.Lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
                }
            }
        }
    }

    public async Task InitializeAsync() => await StartAsync(new LookupPublicOptions());

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

    /// <summary>上限を差し替えて起動し直す。</summary>
    private async Task StartAsync(LookupPublicOptions publicOptions)
    {
        options = publicOptions;
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }

        factory = new ApiFactory(directory, search, logs, options);
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

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        var repository = factory.Services.GetRequiredService<ISurveyRepository>();
        var drafts = factory.Services.GetRequiredService<ISurveyDraftStore>();
        surveyId = Guid.NewGuid();
        publicId = "pub" + Guid.NewGuid().ToString("N")[..12];
        await repository.SaveAsync(new SurveyRecord(
            surveyId, publicId, "検証用", PleasanterSiteId: 1, ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Published, PublishedVersion: null));

        static Question Q(string id) => new()
        {
            QuestionId = id,
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese(id),
        };

        var lookup = new LookupDefinition
        {
            LookupId = "l1",
            AnchorQuestionId = "a1",
            SourceSiteId = 5,
            CredentialId = "cred-1",
            Keys = [new LookupKey
            {
                QuestionId = "k1",
                SourceColumn = "ClassA",
                SourceColumnKind = LookupColumnKind.Text,
            }],
            Targets = [new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" }],
            AllowedColumns = ["ClassB"],
            DisplayFields = [new LookupDisplayField { DisplayId = "city", SourceColumn = "ClassB" }],
        };
        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages = [new Page { PageId = "p1", Questions = [Q("a1"), Q("k1"), Q("t1")] }],
            Lookups = [lookup],
        };
        await drafts.SaveAsync(surveyId, definition, new MappingDefinition(), expectedRevision: 0);
        var draft = await drafts.LoadAsync(surveyId);
        await repository.PublishAsync(surveyId, 1, draft!.Definition, draft.Mapping, null);
    }

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private static async Task<string> NewSessionAsync(HttpClient client, string id)
    {
        using var response = await client.PostAsync($"/api/forms/{id}/lookups/session", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1800, json.GetProperty("expiresInSeconds").GetInt32());
        return json.GetProperty("lookupSession").GetString()!;
    }

    private static Task<HttpResponseMessage> SearchAsync(
        HttpClient client, string id, string lookupId, string json) =>
        client.PostAsync(
            $"/api/forms/{id}/lookups/{lookupId}/search",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static string Body(string session, int version = 1, string key = KeyValue) =>
        JsonSerializer.Serialize(new
        {
            version,
            keys = new Dictionary<string, string> { ["k1"] = key },
            lookupSession = session,
        });

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task 成功は許可列だけを返し_キー値を渡し_no_storeでログに値が出ない()
    {
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        using var response = await SearchAsync(client, publicId, "l1", Body(session));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal("single", json.GetProperty("outcome").GetString());
        var candidate = json.GetProperty("candidates")[0];
        Assert.Equal(FetchedValue, candidate.GetProperty("values").GetProperty("t1").GetString());
        Assert.Equal(FetchedValue, candidate.GetProperty("display").GetProperty("city").GetString());
        // 回答者へ Pleasanter の列名を見せない（Issue #835）
        Assert.DoesNotContain("ClassB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("内部の題名", text, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyValue, text, StringComparison.Ordinal);

        var call = Assert.Single(search.Calls);
        Assert.Equal(surveyId, call.SurveyId);
        Assert.Equal(KeyValue, call.Keys["k1"]);

        // ログに、キー値・取得値・トークンを出さない
        string[] lines;
        lock (logs.Lines)
        {
            lines = [.. logs.Lines];
        }

        Assert.Contains(lines, line => line.Contains("ルックアップの検索", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(KeyValue, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(FetchedValue, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(session, StringComparison.Ordinal));
    }

    [Fact]
    public async Task 入力の不備は外部検索の前に断る()
    {
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);
        string Keys(string extra = "") =>
            "{\"version\":1,\"keys\":{\"k1\":\"v\"},\"lookupSession\":\"" + session + "\"" + extra + "}";

        // 未知のプロパティ
        using (var response = await SearchAsync(client, publicId, "l1", Keys(",\"siteId\":5")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("LookupInvalidKeys", await CodeOf(response));
        }

        // 壊れた JSON・キー無し・キー過多
        foreach (var bad in new[]
        {
            "{",
            "{\"version\":1,\"lookupSession\":\"" + session + "\"}",
            "{\"version\":1,\"keys\":{\"a\":\"1\",\"b\":\"1\",\"c\":\"1\",\"d\":\"1\",\"e\":\"1\",\"f\":\"1\"},\"lookupSession\":\""
                + session + "\"}",
            "{\"version\":1,\"keys\":{\"k1\":\"1\",\"k1\":\"2\"},\"lookupSession\":\"" + session + "\"}",
        })
        {
            using var response = await SearchAsync(client, publicId, "l1", bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // 本文の上限超過は 413（422 とは別）
        using (var response = await SearchAsync(
            client, publicId, "l1", Body(session, key: new string('x', 5000))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal("LookupRequestTooLarge", await CodeOf(response));
        }

        // トークンの不備
        using (var response = await SearchAsync(client, publicId, "l1", Body("l1.0.x.y")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("LookupSessionInvalid", await CodeOf(response));
        }

        // 別のフォームのトークン
        var other = await NewSessionAsync(client, "another-form");
        using (var response = await SearchAsync(client, publicId, "l1", Body(other)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(search.Calls);
    }

    [Fact]
    public async Task 版の不一致_不明な検索_不明な公開IDを区別して返す()
    {
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        using (var response = await SearchAsync(client, publicId, "l1", Body(session, version: 2)))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("LookupVersionChanged", await CodeOf(response));
        }

        using (var response = await SearchAsync(client, publicId, "nope", Body(session)))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("LookupUnavailable", await CodeOf(response));
        }

        var unknown = await NewSessionAsync(client, "no-such-form");
        using (var response = await SearchAsync(client, "no-such-form", "l1", Body(unknown)))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("LookupUnavailable", await CodeOf(response));
        }

        Assert.Empty(search.Calls);
    }

    [Theory]
    [InlineData(LookupSearchStatus.TooManyResults, HttpStatusCode.UnprocessableEntity, "LookupTooManyResults")]
    [InlineData(LookupSearchStatus.SourceInvalid, HttpStatusCode.BadGateway, "LookupSourceInvalid")]
    [InlineData(LookupSearchStatus.TemporarilyUnavailable, HttpStatusCode.ServiceUnavailable, "LookupTemporarilyUnavailable")]
    [InlineData(LookupSearchStatus.Timeout, HttpStatusCode.GatewayTimeout, "LookupTimeout")]
    [InlineData(LookupSearchStatus.InvalidKeys, HttpStatusCode.BadRequest, "LookupInvalidKeys")]
    public async Task 検索の失敗はHTTPとコードへ写し_候補を返さない(
        LookupSearchStatus status, HttpStatusCode http, string code)
    {
        search.Behave = () => Task.FromResult(new LookupSearchResult
        {
            Status = status,
            Failure = LookupSearchFailure.CredentialDisabled,
            InvalidKeyQuestionIds = ["k1", "attacker-supplied"],
            Candidates = [new LookupCandidateRow(new Dictionary<string, string?> { ["ClassB"] = FetchedValue })],
        });
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        using var response = await SearchAsync(client, publicId, "l1", Body(session));

        Assert.Equal(http, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(FetchedValue, text, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialDisabled", text, StringComparison.Ordinal);
        if (status == LookupSearchStatus.InvalidKeys)
        {
            Assert.Equal(
                ["k1"],
                JsonDocument.Parse(text).RootElement.GetProperty("fieldIds")
                    .EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
    }

    [Fact]
    public async Task 想定外の例外は502で_例外の中身を応答にもログにも出さない()
    {
        search.Behave = () => throw new InvalidOperationException("DETAIL-" + KeyValue);
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        using var response = await SearchAsync(client, publicId, "l1", Body(session));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(KeyValue, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        lock (logs.Lines)
        {
            Assert.DoesNotContain(logs.Lines, line => line.Contains(KeyValue, StringComparison.Ordinal));
            Assert.Contains(logs.Lines, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task 参照元の不備の502は管理者の通知へ立て_連続しても一件で_値や秘密は出ない()
    {
        search.Behave = () => Task.FromResult(new LookupSearchResult
        {
            Status = LookupSearchStatus.SourceInvalid,
            Failure = LookupSearchFailure.Unauthorized,
        });
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        for (var i = 0; i < 5; i++)
        {
            using var response = await SearchAsync(client, publicId, "l1", Body(session));
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal("LookupSourceInvalid", await CodeOf(response));
        }

        var store = factory.Services.GetRequiredService<IAdminNotificationStore>();
        var rows = await store.ListAsync(new AdminNotificationQuery { UnreadOnly = true });
        var row = Assert.Single(rows, item => item.Kind == (int)Core.Notifications.AdminNotificationKind.LookupSourceAccessDenied);
        Assert.Equal(surveyId, row.SurveyId);

        // 窓の内側では DB へ足さないので、件数は 1 のまま
        Assert.Equal(1, row.Count);

        // Issue #836。検索 ID と相関 ID が通知の行に載る。秘密・値は載らない
        Assert.Equal("l1", row.LookupId);
        Assert.False(string.IsNullOrWhiteSpace(row.LastCorrelationId));
        Assert.DoesNotContain(KeyValue, row.LookupId, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyValue, row.LastCorrelationId, StringComparison.Ordinal);
        Assert.DoesNotContain(session, row.LastCorrelationId, StringComparison.Ordinal);
        lock (logs.Lines)
        {
            Assert.DoesNotContain(logs.Lines, line => line.Contains(KeyValue, StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Lines, line => line.Contains(session, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task 想定外の例外の502も知らせるが_502以外の失敗では知らせない()
    {
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);
        var store = factory.Services.GetRequiredService<IAdminNotificationStore>();

        foreach (var status in new[]
        {
            LookupSearchStatus.TemporarilyUnavailable,
            LookupSearchStatus.Timeout,
            LookupSearchStatus.TooManyResults,
            LookupSearchStatus.InvalidKeys,
        })
        {
            search.Behave = () => Task.FromResult(new LookupSearchResult
            {
                Status = status,
                Failure = LookupSearchFailure.Unauthorized,
            });
            using var response = await SearchAsync(client, publicId, "l1", Body(session));
            Assert.NotEqual(HttpStatusCode.BadGateway, response.StatusCode);
        }

        Assert.Empty(await store.ListAsync(new AdminNotificationQuery()));

        search.Behave = () => throw new InvalidOperationException("DETAIL-" + KeyValue);
        using var failed = await SearchAsync(client, publicId, "l1", Body(session));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        var row = Assert.Single(await store.ListAsync(new AdminNotificationQuery()));
        Assert.Equal((int)Core.Notifications.AdminNotificationKind.LookupSourceBadResponse, row.Kind);
    }

    [Fact]
    public async Task 通知を書けなくても応答は同じ502()
    {
        search.Behave = () => Task.FromResult(new LookupSearchResult
        {
            Status = LookupSearchStatus.SourceInvalid,
            Failure = LookupSearchFailure.CredentialDisabled,
        });
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        // 通知の表を壊して、書き込みが失敗する状況を作る
        await using (var connection = new SqliteConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE AdminNotifications";
            await command.ExecuteNonQueryAsync();
        }

        using var response = await SearchAsync(client, publicId, "l1", Body(session));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("LookupSourceInvalid", await CodeOf(response));
        Assert.DoesNotContain("CredentialDisabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 回答者の枠を超えたら429でRetryAfterを付け_外部検索しない()
    {
        await StartAsync(new LookupPublicOptions { RespondentLimit = 2 });
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        for (var i = 0; i < 2; i++)
        {
            using var ok = await SearchAsync(client, publicId, "l1", Body(session));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await SearchAsync(client, publicId, "l1", Body(session));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
        Assert.Equal("LookupRateLimited", await CodeOf(limited));
        Assert.Equal(2, search.Calls.Count);

        // 別のトークン（別の回答者）の枠は別
        var another = await NewSessionAsync(client, publicId);
        using var fresh = await SearchAsync(client, publicId, "l1", Body(another));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task IPの枠とフォームの枠も検索の前に効き_トークンの発行にも効く()
    {
        await StartAsync(new LookupPublicOptions { IpLimit = 3, FormLimit = 1000 });
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        // 発行（IP 枠は検索と別の数え先）で 1 回、検索で 3 回まで
        for (var i = 0; i < 3; i++)
        {
            using var ok = await SearchAsync(client, publicId, "l1", Body(session));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await SearchAsync(client, publicId, "l1", Body(session));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(3, search.Calls.Count);

        await StartAsync(new LookupPublicOptions { FormLimit = 2 });
        using var second = Client();
        _ = await NewSessionAsync(second, publicId);
        _ = await NewSessionAsync(second, publicId);
        using var issue = await second.PostAsync($"/api/forms/{publicId}/lookups/session", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, issue.StatusCode);
    }

    [Fact]
    public async Task 同時実行数が満了なら503で参照元を呼ばず_終われば戻る()
    {
        await StartAsync(new LookupPublicOptions { ConcurrentSearchesPerForm = 1 });
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        search.Behave = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            return new LookupSearchResult { Status = LookupSearchStatus.Success };
        };
        using var client = Client();
        var session = await NewSessionAsync(client, publicId);

        var first = SearchAsync(client, publicId, "l1", Body(session));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var busy = await SearchAsync(client, publicId, "l1", Body(session)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal("LookupTemporarilyUnavailable", await CodeOf(busy));
        }

        Assert.Single(search.Calls);
        release.SetResult();
        using (var done = await first)
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        search.Behave = () => Task.FromResult(new LookupSearchResult { Status = LookupSearchStatus.Success });
        using var after = await SearchAsync(client, publicId, "l1", Body(session));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task 下書きだけで未公開のアンケートは検索できない()
    {
        var repository = factory.Services.GetRequiredService<ISurveyRepository>();
        var draftPublicId = "dr" + Guid.NewGuid().ToString("N")[..12];
        await repository.SaveAsync(new SurveyRecord(
            Guid.NewGuid(), draftPublicId, "下書き", 1, null, (int)SurveyStatus.Draft, null));
        using var client = Client();
        var session = await NewSessionAsync(client, draftPublicId);

        using var response = await SearchAsync(client, draftPublicId, "l1", Body(session));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(search.Calls);
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
        private readonly FakeSearch search;
        private readonly CapturingLoggerProvider logs;
        private readonly LookupPublicOptions publicOptions;

        public ApiFactory(
            string directory,
            FakeSearch search,
            CapturingLoggerProvider logs,
            LookupPublicOptions publicOptions)
        {
            this.directory = directory;
            this.search = search;
            this.logs = logs;
            this.publicOptions = publicOptions;
            Directory.CreateDirectory(Path.Combine(directory, "wwwroot"));
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "lookup.db"),
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
                    ["QUESTIONNAIRE_PLEASANTER_BASEURL"] = "https://localhost.invalid",
                    ["QUESTIONNAIRE_PLEASANTER_APIKEY"] = "contract-test-not-a-real-key",
                }));
        }

        public string ConnectionString { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(directory);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
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

                services.RemoveAll<ILookupSearchService>();
                services.AddSingleton<ILookupSearchService>(search);
                services.RemoveAll<LookupPublicOptions>();
                services.AddSingleton(publicOptions);
            });
        }
    }
}
