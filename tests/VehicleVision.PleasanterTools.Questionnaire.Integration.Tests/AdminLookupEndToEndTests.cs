using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// ルックアップの管理 API を、実アプリ（SQLite）と偽の点検・偽の検索で通しで確かめる（Issue #879）。
/// <b>実機の Pleasanter は使わない。</b>
/// </summary>
public sealed class AdminLookupEndToEndTests : IAsyncLifetime
{
    private const string ApiKey = "LOOKUP-API-KEY-SECRET-1a2b3c";
    private const string FetchedValue = "FETCHED-東京都-7d2b";

    private readonly FakeInspection inspection = new();
    private readonly FakeSearch search = new();
    private AdminContractHost host = null!;
    private Guid surveyId;

    private sealed class FakeInspection : ILookupSiteInspectionService
    {
        public LookupSiteInspection Result { get; set; } = new()
        {
            Status = LookupSearchStatus.Success,
            Columns =
            [
                new LookupSourceColumn("ClassA", LookupColumnKind.Text),
                new LookupSourceColumn("ClassB", LookupColumnKind.Text),
            ],
        };

        public Task<LookupSiteInspection> InspectAsync(
            Guid surveyId, string credentialId, long siteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class FakeSearch : ILookupSearchService
    {
        public List<Dictionary<string, string?>> Calls { get; } = [];

        public Task<LookupSearchResult> SearchAsync(
            Guid surveyId,
            LookupDefinition lookup,
            IReadOnlyDictionary<string, string?> keyValues,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Dictionary<string, string?>(keyValues));
            return Task.FromResult(new LookupSearchResult
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
        }
    }

    public async Task InitializeAsync()
    {
        host = await AdminContractHost.StartAsync(services =>
        {
            services.RemoveAll<ILookupSiteInspectionService>();
            services.AddSingleton<ILookupSiteInspectionService>(inspection);
            services.RemoveAll<ILookupSearchService>();
            services.AddSingleton<ILookupSearchService>(search);
        });
    }

    public async Task DisposeAsync() => await host.DisposeAsync();

    private async Task<HttpClient> SignInAsync(string role)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var password = "contract-" + Guid.NewGuid().ToString("N");
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        var hash = host.Services.GetRequiredService<PasswordHasher>().Hash(password);
        Assert.NotNull(await store.CreateAsync(loginId, hash, role, "en"));
        var client = host.CreateClient();
        using var response = await host.LoginAsync(client, new SeededAdmin(loginId, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task SeedSurveyAsync(string? credentialId)
    {
        var repository = host.Services.GetRequiredService<ISurveyRepository>();
        var drafts = host.Services.GetRequiredService<ISurveyDraftStore>();
        surveyId = Guid.NewGuid();
        await repository.SaveAsync(new SurveyRecord(
            surveyId, "pub" + Guid.NewGuid().ToString("N")[..12], "検証用", PleasanterSiteId: 1,
            ResponseJsonColumn: null, Status: (int)SurveyStatus.Draft, PublishedVersion: null));

        static Question Q(string id) => new()
        {
            QuestionId = id,
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese(id),
        };

        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages = [new Page { PageId = "p1", Questions = [Q("a1"), Q("k1"), Q("t1")] }],
            Lookups =
            [
                new LookupDefinition
                {
                    LookupId = "l1",
                    AnchorQuestionId = "a1",
                    SourceSiteId = 5,
                    CredentialId = credentialId,
                    Keys = [new LookupKey
                    {
                        QuestionId = "k1",
                        SourceColumn = "ClassA",
                        SourceColumnKind = LookupColumnKind.Text,
                    }],
                    Targets = [new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" }],
                    AllowedColumns = ["ClassB"],
                    DisplayFields = [new LookupDisplayField { DisplayId = "city", SourceColumn = "ClassB" }],
                },
            ],
        };
        await drafts.SaveAsync(surveyId, definition, new MappingDefinition(), expectedRevision: 0);
    }

    private string Url(string path) => $"/api/admin/surveys/{surveyId}/lookups{path}";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task 資格情報の登録_再入力_失効_解除で秘密を一度も返さない()
    {
        await SeedSurveyAsync(null);
        using var client = await SignInAsync("Administrator");

        using var created = await client.PostAsJsonAsync(Url("/credentials"), new { apiKey = ApiKey });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiKey, createdText, StringComparison.Ordinal);
        var id = JsonDocument.Parse(createdText).RootElement.GetProperty("credentialId").GetString()!;

        var credentials = host.Services.GetRequiredService<ILookupCredentialStore>();
        Assert.Equal(ApiKey, (await credentials.ResolveSecretAsync(surveyId, id)).Secret);

        // 再入力（ローテーション）
        const string Rotated = "ROTATED-KEY-9z8y7x";
        using (var rotated = await client.PutAsJsonAsync(Url($"/credentials/{id}"), new { apiKey = Rotated }))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            var text = await rotated.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Rotated, text, StringComparison.Ordinal);
        }

        Assert.Equal(Rotated, (await credentials.ResolveSecretAsync(surveyId, id)).Secret);

        // 一覧にも秘密は出ない
        using (var list = await client.GetAsync(Url("/credentials")))
        {
            var text = await list.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Rotated, text, StringComparison.Ordinal);
            Assert.DoesNotContain("secretCipher", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(id, JsonDocument.Parse(text).RootElement[0].GetProperty("credentialId").GetString());
        }

        // 失効すると実行時に取り出せない
        using (var disabled = await client.PutAsJsonAsync(Url($"/credentials/{id}/enabled"), new { enabled = false }))
        {
            Assert.False((await JsonOf(disabled)).GetProperty("isEnabled").GetBoolean());
        }

        Assert.Equal(LookupSecretStatus.Disabled, (await credentials.ResolveSecretAsync(surveyId, id)).Status);

        using (var deleted = await client.DeleteAsync(Url($"/credentials/{id}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.Equal(LookupSecretStatus.NotFound, (await credentials.ResolveSecretAsync(surveyId, id)).Status);
    }

    [Fact]
    public async Task 不正なAPIキーは保存しない()
    {
        await SeedSurveyAsync(null);
        using var client = await SignInAsync("Administrator");

        foreach (var bad in new[] { "", "   ", new string('x', 513), "ab\ncd" })
        {
            using var response = await client.PostAsJsonAsync(Url("/credentials"), new { apiKey = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var credentials = host.Services.GetRequiredService<ILookupCredentialStore>();
        Assert.Empty(await credentials.ListStatusAsync(surveyId));
    }

    [Fact]
    public async Task 編集者は秘密も接続試験も設定の変更もできない()
    {
        await SeedSurveyAsync("cred-x");
        using var editor = await SignInAsync("Editor");

        using (var create = await editor.PostAsJsonAsync(Url("/credentials"), new { apiKey = ApiKey }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var inspect = await editor.PostAsJsonAsync(Url("/inspect"), new { credentialId = "cred-x", sourceSiteId = 5 }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, inspect.StatusCode);
        }

        // 一覧は見られる
        using (var list = await editor.GetAsync(Url("/credentials")))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        // 下書きの保存でルックアップを変えられない。変えなければ保存できる
        using var draftResponse = await editor.GetAsync($"/api/admin/surveys/{surveyId}");
        var draft = await JsonOf(draftResponse);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(draft.GetRawText())!;
        using (var same = await editor.PutAsJsonAsync($"/api/admin/surveys/{surveyId}", new
        {
            revision = draft.GetProperty("revision").GetInt32(),
            definition = body["definition"],
            mapping = body["mapping"],
        }))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        }

        var definition = JsonSerializer.Deserialize<SurveyDefinition>(
            body["definition"].GetRawText(), SurveyJson.Options)!;
        var changed = definition with
        {
            Lookups = [.. definition.GetLookups().Select(lookup => lookup with { MaxResults = 3 })],
        };
        using var forbidden = await editor.PutAsJsonAsync($"/api/admin/surveys/{surveyId}", new
        {
            revision = draft.GetProperty("revision").GetInt32() + 1,
            definition = changed,
            mapping = body["mapping"],
        }, SurveyJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task 接続試験は分類と列だけを返す()
    {
        await SeedSurveyAsync("cred-x");
        using var client = await SignInAsync("SurveyAdministrator");

        using var ok = await client.PostAsJsonAsync(Url("/inspect"), new { credentialId = "cred-x", sourceSiteId = 5 });
        var text = await ok.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(text).RootElement;
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Contains(json.GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "ClassA");

        inspection.Result = new LookupSiteInspection
        {
            Status = LookupSearchStatus.SourceInvalid,
            Failure = LookupSearchFailure.Forbidden,
        };
        using var failed = await client.PostAsJsonAsync(Url("/inspect"), new { credentialId = "cred-x", sourceSiteId = 5 });
        var failure = await JsonOf(failed);
        Assert.False(failure.GetProperty("ok").GetBoolean());
        Assert.Equal("Forbidden", failure.GetProperty("failure").GetString());
        Assert.Equal(0, failure.GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task テスト公開は不備のあるルックアップを拒否する()
    {
        await SeedSurveyAsync("no-such-credential");
        using var client = await SignInAsync("Administrator");

        using var response = await client.PostAsync($"/api/admin/surveys/{surveyId}/test-publish", content: null);

        // マッピングの不備が先に出ることがある。ルックアップの不備は専用の応答で確認する
        using var problems = await client.GetAsync(Url("/problems"));
        Assert.Equal(HttpStatusCode.OK, problems.StatusCode);
        var list = await JsonOf(problems);
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("code").GetString() == "CredentialNotFound");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task プレビュー検索は保存済みの下書きを使い_許可列だけを返す()
    {
        await SeedSurveyAsync("cred-x");
        using var client = await SignInAsync("Editor");

        using var response = await client.PostAsJsonAsync(
            Url("/l1/preview-search"), new { keys = new Dictionary<string, string> { ["k1"] = "KEY-1" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ClassB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("内部の題名", text, StringComparison.Ordinal);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("single", json.GetProperty("outcome").GetString());
        Assert.Equal(FetchedValue, json.GetProperty("candidates")[0].GetProperty("values").GetProperty("t1").GetString());
        Assert.Equal("KEY-1", Assert.Single(search.Calls)["k1"]);

        using var unknown = await client.PostAsJsonAsync(
            Url("/nope/preview-search"), new { keys = new Dictionary<string, string> { ["k1"] = "x" } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task 認証なしでは何も見えない()
    {
        await SeedSurveyAsync("cred-x");
        using var client = host.CreateClient();

        using var list = await client.GetAsync(Url("/credentials"));
        using var create = await client.PostAsJsonAsync(Url("/credentials"), new { apiKey = ApiKey });
        using var preview = await client.PostAsJsonAsync(Url("/l1/preview-search"), new { keys = new { k1 = "x" } });

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);
    }
}
