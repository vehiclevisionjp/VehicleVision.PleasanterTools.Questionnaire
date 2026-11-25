using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// エクスポート（<c>GET .../export</c>）・インポート（<c>POST .../import</c>）を、個人トークンで使う入口を、
/// 実アプリの host（SQLite）へ HTTP で当てて確かめる（Issue #1029。親 #1012）。
/// </summary>
/// <remarks>
/// 見るのは「エクスポートは <c>surveys.read</c>・インポートは <c>surveys.publish</c> で通る（他のスコープは 403）」
/// 「<b>トークンではルックアップを持ち込めない</b>（403 で断り、1 行も作らない）」
/// 「エクスポート・インポートの監査にトークンの識別が付く」。<b>SQLite だけ</b>で動く。
/// </remarks>
public sealed class AdminApiTokenExchangeEndToEndTests
{
    private const string Password = "contract-long-password-1";
    private const string Surveys = "/api/admin/surveys";

    private static readonly IReadOnlyDictionary<string, string> Enabled =
        new Dictionary<string, string> { [AdminApiTokenOptions.ModeSetting] = "enabled" };

    private sealed record Account(Guid Id, string LoginId);

    private static async Task<Account> SeedAsync(AdminContractHost host, AdminRole role)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var user = await host.Services.GetRequiredService<IAdminUserStore>().CreateAsync(
            loginId, host.Services.GetRequiredService<PasswordHasher>().Hash(Password), AdminRoleMap.ToName(role), "en");
        Assert.NotNull(user);
        return new Account(user.Id, loginId);
    }

    private static async Task<(string Token, Guid TokenId, string Prefix)> IssueAsync(
        AdminContractHost host,
        Account owner,
        params string[] scopes)
    {
        var (outcome, issued) = await host.Services.GetRequiredService<AdminApiTokenService>().IssueAsync(
            owner.Id,
            new AdminApiTokenIssueRequest("ci", scopes, 30, []),
            new AdminApiTokenReauthentication.Password(Password));
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, outcome);
        return (issued!.Token, issued.Record.TokenId, issued.Record.Prefix);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? token,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static SurveyDefinition Definition(string surveyId, bool withLookup) => new()
    {
        SurveyId = surveyId,
        Version = 1,
        Title = LocalizedText.Japanese("相談会"),
        Lookups = withLookup
            ? [new LookupDefinition { LookupId = "l1", AnchorQuestionId = "q1", SourceSiteId = 100 }]
            : [],
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
                        Title = LocalizedText.Japanese("参加しますか"),
                        Choices = [new Choice("yes", LocalizedText.Japanese("はい"))],
                    },
                ],
            },
        ],
    };

    /// <summary>エクスポートした内容（インポートの <c>document</c> に入れる JSON）。</summary>
    private static JsonNode Document(bool withLookup) =>
        JsonNode.Parse(SurveyExchange.Export(Definition(Guid.NewGuid().ToString(), withLookup), new MappingDefinition()))!;

    private static async Task<Guid> SeedSurveyAsync(AdminContractHost host)
    {
        var surveyId = Guid.NewGuid();
        await host.Services.GetRequiredService<ISurveyRepository>().SaveAsync(new SurveyRecord(
            surveyId, SurveyPublicId.Generate(), "相談会", 1, null, (int)SurveyStatus.Draft, PublishedVersion: null));
        await host.Services.GetRequiredService<ISurveyDraftStore>().SaveAsync(
            surveyId, Definition(surveyId.ToString(), withLookup: false), new MappingDefinition(), expectedRevision: 0);
        return surveyId;
    }

    private static async Task<int> CountSurveysAsync(AdminContractHost host) =>
        (await host.Services.GetRequiredService<ISurveyDraftStore>().ListAsync(new SurveyListQuery { Limit = 500 })).Count;

    private static async Task<IReadOnlyList<AuditEntry>> AuditAsync(AdminContractHost host, string? action = null) =>
        await host.Services.GetRequiredService<IAuditLogStore>()
            .ListAsync(new AuditLogQuery { Limit = 500, Action = action });

    private static Dictionary<string, string> DetailOf(AuditEntry entry) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(entry.DetailJson!)!;

    // ---- エクスポート --------------------------------------------------------------------------

    [Fact]
    public async Task 書き出しは_surveys_readのトークンで通り_内容は人の画面と同じ()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var surveyId = await SeedSurveyAsync(host);
        var (token, _, _) = await IssueAsync(host, await SeedAsync(host, AdminRole.Editor), "surveys.read");
        using var client = host.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, $"{Surveys}/{surveyId}/export", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var parsed = SurveyExchange.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(parsed.IsSuccess);
        Assert.Equal("相談会", parsed.Document!.Definition.Title.Get("ja"));
    }

    [Fact]
    public async Task 書き出しは_surveys_readが無いトークンでは403_存在しないアンケートは404()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var surveyId = await SeedSurveyAsync(host);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (writeOnly, _, _) = await IssueAsync(host, owner, "surveys.write", "surveys.publish");
        var (read, _, _) = await IssueAsync(host, owner, "surveys.read");
        using var client = host.CreateClient();

        using var forbidden = await SendAsync(client, HttpMethod.Get, $"{Surveys}/{surveyId}/export", writeOnly);
        using var missing = await SendAsync(client, HttpMethod.Get, $"{Surveys}/{Guid.NewGuid()}/export", read);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 書き出しは_トークンのときだけ監査へ残り_識別が付く_人の書き出しは残さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var surveyId = await SeedSurveyAsync(host);
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var (token, tokenId, prefix) = await IssueAsync(host, owner, "surveys.read");
        using var client = host.CreateClient();

        using var byToken = await SendAsync(client, HttpMethod.Get, $"{Surveys}/{surveyId}/export", token);
        Assert.Equal(HttpStatusCode.OK, byToken.StatusCode);

        var entry = Assert.Single(await AuditAsync(host, "GET /api/admin/surveys/{surveyId}/export"));
        Assert.Equal("GET /api/admin/surveys/{surveyId}/export", entry.Action);
        Assert.True(entry.Succeeded);
        Assert.Equal("Survey", entry.TargetType);
        Assert.Equal(surveyId.ToString(), entry.TargetId);
        Assert.Equal(owner.Id.ToString("D"), entry.ActorKey);
        Assert.Equal("api-token", DetailOf(entry)["actorKind"]);
        Assert.Equal(tokenId.ToString(), DetailOf(entry)["tokenId"]);
        Assert.Equal(prefix, DetailOf(entry)["tokenPrefix"]);
        Assert.DoesNotContain(token[prefix.Length..], entry.DetailJson, StringComparison.Ordinal);

        // 人（cookie）のエクスポートは、今までどおり残さない
        using var human = host.CreateClient();
        using var login = await human.PostAsJsonAsync(
            "/api/admin/login", new { loginId = owner.LoginId, password = Password });
        login.EnsureSuccessStatusCode();
        using var byHuman = await human.GetAsync($"{Surveys}/{surveyId}/export");
        Assert.Equal(HttpStatusCode.OK, byHuman.StatusCode);
        Assert.Single(await AuditAsync(host, "GET /api/admin/surveys/{surveyId}/export"));
    }

    // ---- インポート --------------------------------------------------------------------------

    [Fact]
    public async Task 取り込みは_surveys_publishのトークンで通り_新しい下書きができ_監査にトークンの識別が付く()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (token, tokenId, prefix) = await IssueAsync(host, owner, "surveys.publish");
        using var client = host.CreateClient();

        using var response = await SendAsync(
            client, HttpMethod.Post, $"{Surveys}/import", token,
            new { document = Document(withLookup: false), pleasanterSiteId = 12345 });

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        var surveyId = JsonNode.Parse(text)!["surveyId"]!.GetValue<Guid>();
        Assert.Equal(1, await CountSurveysAsync(host));

        var entry = Assert.Single(await AuditAsync(host), item => item.Action == "POST /api/admin/surveys/import");
        Assert.True(entry.Succeeded);
        // インポートの対象の種別は、既存の書き込み（複製など）と同じ小文字
        Assert.Equal("survey", entry.TargetType);
        Assert.Equal(surveyId.ToString(), entry.TargetId);
        Assert.Equal(owner.Id.ToString("D"), entry.ActorKey);
        Assert.Equal("api-token", DetailOf(entry)["actorKind"]);
        Assert.Equal(tokenId.ToString(), DetailOf(entry)["tokenId"]);
        Assert.Equal(prefix, DetailOf(entry)["tokenPrefix"]);
    }

    [Fact]
    public async Task 取り込みは_surveys_publish以外のスコープでは403_1行も作らない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (readWrite, _, _) = await IssueAsync(host, owner, "surveys.read", "surveys.write");
        using var client = host.CreateClient();

        using var response = await SendAsync(
            client, HttpMethod.Post, $"{Surveys}/import", readWrite,
            new { document = Document(withLookup: false), pleasanterSiteId = 12345 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountSurveysAsync(host));
    }

    [Fact]
    public async Task 持ち主の役割に公開が無ければ_surveys_publishを付けても取り込めない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (token, _, _) = await IssueAsync(host, owner, "surveys.read", "surveys.publish");
        await host.Services.GetRequiredService<IAdminUserStore>()
            .SetRoleAsync(owner.Id, AdminRoleMap.ToName(AdminRole.Editor));
        using var client = host.CreateClient();

        using var response = await SendAsync(
            client, HttpMethod.Post, $"{Surveys}/import", token,
            new { document = Document(withLookup: false), pleasanterSiteId = 12345 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountSurveysAsync(host));
    }

    [Fact]
    public async Task ルックアップを含む取り込みは_トークンでは403で断り_検査だけの要求でも断る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        // **アンケート管理者の持ち主でも**（役割はルックアップを持ち込める。トークンでは持ち込めない）
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var (token, _, _) = await IssueAsync(host, owner, "surveys.publish");
        using var client = host.CreateClient();

        using var created = await SendAsync(
            client, HttpMethod.Post, $"{Surveys}/import", token,
            new { document = Document(withLookup: true), pleasanterSiteId = 12345 });
        using var dryRun = await SendAsync(
            client, HttpMethod.Post, $"{Surveys}/import", token,
            new { document = Document(withLookup: true), pleasanterSiteId = 12345, dryRun = true });

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dryRun.StatusCode);
        Assert.Contains("message", await created.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await CountSurveysAsync(host));

        // 失敗も、トークンの識別つきで残る
        var failures = (await AuditAsync(host, "POST /api/admin/surveys/import")).Where(item => !item.Succeeded).ToList();
        Assert.Equal(2, failures.Count);
        Assert.All(failures, item => Assert.Equal(403, item.StatusCode));
        Assert.All(failures, item => Assert.Equal("api-token", DetailOf(item)["actorKind"]));
    }

    [Fact]
    public async Task ルックアップを含む取り込みは_人のセッションでは今までどおり通る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        using var human = host.CreateClient();
        using var login = await human.PostAsJsonAsync(
            "/api/admin/login", new { loginId = owner.LoginId, password = Password });
        login.EnsureSuccessStatusCode();

        using var response = await human.PostAsJsonAsync(
            $"{Surveys}/import", new { document = Document(withLookup: true), pleasanterSiteId = 12345, dryRun = true });

        // 検査の結果（ルックアップの構造の不備）で 400 になることはあるが、**権限では断らない**
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
