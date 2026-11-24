using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理 API の個人トークン（API キー）を <c>Authorization: Bearer</c> で使う入口を、実アプリの host（SQLite）へ HTTP で当てて確かめる
/// （Issue #1026。親 #1012）。
/// </summary>
/// <remarks>
/// <para>
/// 見るのは「トークンを許す口だけが開き、それ以外は開かない」「Bearer がある要求は cookie を評価しない」
/// 「失敗の応答を区別しない」「CSRF の除外を足していない」。
/// 口が<b>認証を通った</b>かは、存在しないアンケートへ当てて <c>404</c> が返る（<c>401</c>・<c>403</c> でない）ことで見る
/// （実際にアンケートを書き換えない）。
/// </para>
/// <para>
/// <b>SQLite だけ</b>で動く。<b>接続元 IP は TestServer では決められない</b>ので、許可リストは「外れる」側だけを見る
/// （「入る」側は <c>AdminApiTokenServiceTests</c>）。
/// </para>
/// </remarks>
public sealed class AdminApiTokenBearerEndToEndTests
{
    private const string Password = "contract-long-password-1";
    private const string Surveys = "/api/admin/surveys";

    private static readonly IReadOnlyDictionary<string, string> Enabled =
        new Dictionary<string, string> { [AdminApiTokenOptions.ModeSetting] = "enabled" };

    private static string Missing(string suffix = "") => $"{Surveys}/{Guid.NewGuid()}{suffix}";

    private sealed record Account(Guid Id, string LoginId);

    private static async Task<Account> SeedAsync(AdminContractHost host, AdminRole role)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var user = await host.Services.GetRequiredService<IAdminUserStore>().CreateAsync(
            loginId, host.Services.GetRequiredService<PasswordHasher>().Hash(Password), AdminRoleMap.ToName(role), "en");
        Assert.NotNull(user);
        return new Account(user.Id, loginId);
    }

    /// <summary>サービスで発行する（画面の本人確認はパスワード）。</summary>
    private static async Task<string> IssueAsync(
        AdminContractHost host,
        Account owner,
        params string[] scopes)
    {
        var (outcome, issued) = await host.Services.GetRequiredService<AdminApiTokenService>().IssueAsync(
            owner.Id,
            new AdminApiTokenIssueRequest("ci", scopes, 30, []),
            new AdminApiTokenReauthentication.Password(Password));
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, outcome);
        return issued!.Token;
    }

    /// <summary>期限切れ・許可リスト付きなど、サービスでは作れない形のトークンを、ストアへ直接作る。</summary>
    private static async Task<string> InsertAsync(
        AdminContractHost host,
        Account owner,
        string[] scopes,
        DateTime? expiresAt = null,
        string[]? ipAllowList = null)
    {
        var secret = AdminApiTokenSecret.Create();
        var now = DateTime.UtcNow;
        await host.Services.GetRequiredService<IAdminApiTokenStore>().CreateAsync(new NewAdminApiToken(
            Guid.NewGuid(), owner.Id, "direct", secret.Prefix, secret.Hash, scopes, ipAllowList ?? [],
            now.AddDays(-10), expiresAt ?? now.AddDays(10)));
        return secret.Plain;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        string? token,
        Action<HttpRequestMessage>? configure = null,
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

        configure?.Invoke(request);
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? token) =>
        SendAsync(client, HttpMethod.Get, url, token);

    private static async Task<HttpClient> CookieClientAsync(AdminContractHost host, Account account)
    {
        var client = host.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/admin/login", new { loginId = account.LoginId, password = Password });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static void AssertPassedAuthentication(HttpResponseMessage response) =>
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    // ---- 通る --------------------------------------------------------------------------

    [Fact]
    public async Task 正しいトークンで_許された取得の口に入れる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var token = await IssueAsync(host, await SeedAsync(host, AdminRole.Administrator), "surveys.read");
        using var client = host.CreateClient();

        using var list = await GetAsync(client, Surveys, token);
        using var detail = await GetAsync(client, Missing(), token);

        Assert.True(list.StatusCode == HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        AssertPassedAuthentication(detail);
        Assert.True(list.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task 更新と公開の口は_宣言したスコープがあれば通る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var write = await IssueAsync(host, owner, "surveys.write");
        var publish = await IssueAsync(host, owner, "surveys.publish");
        using var client = host.CreateClient();

        // **群の「閲覧」を別に要らない**（口が宣言したスコープで判定する）。書き込みだけのトークンが更新できる
        using var put = await SendAsync(client, HttpMethod.Put, Missing(), write, body: new { });
        AssertPassedAuthentication(put);

        foreach (var action in new[] { "test-publish", "test-republish", "publish", "suspend", "resume" })
        {
            using var response = await SendAsync(client, HttpMethod.Post, Missing($"/{action}"), publish, body: new { });
            AssertPassedAuthentication(response);
        }
    }

    // ---- 401: 失敗は区別しない ----------------------------------------------------------

    [Fact]
    public async Task 偽の_期限切れ_失効_停止した持ち主_許可リスト外_形の違うトークンは_同じ401()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var service = host.Services.GetRequiredService<AdminApiTokenService>();

        var expired = await InsertAsync(host, owner, ["surveys.read"], expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var revoked = await IssueAsync(host, owner, "surveys.read");
        var revokedRecord = (await service.ListAsync(owner.Id)).Single(item => item.Name == "ci");
        Assert.NotNull(await service.RevokeAsync(revokedRecord.TokenId, owner.Id, owner.Id, "test"));
        var outsideIp = await InsertAsync(host, owner, ["surveys.read"], ipAllowList: ["203.0.113.0/24"]);

        var disabledOwner = await SeedAsync(host, AdminRole.Administrator);
        var disabled = await IssueAsync(host, disabledOwner, "surveys.read");
        Assert.Equal(
            AdminChangeResult.Updated,
            await host.Services.GetRequiredService<IAdminUserStore>().SetDisabledAsync(disabledOwner.Id, true));

        var forged = AdminApiTokenSecret.Create().Plain;
        using var client = host.CreateClient();

        var bodies = new List<string>();
        foreach (var token in new[] { forged, expired, revoked, outsideIp, disabled, "qat_short", "not-a-token", "" })
        {
            using var response = await GetAsync(client, Surveys, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        // **応答の本文も同じ。** 理由を外へ出さない
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task 機能が無効のときは_トークンを一切受けない()
    {
        await using var host = await AdminContractHost.StartAsync();
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var token = await InsertAsync(host, owner, ["surveys.read"]);
        using var client = host.CreateClient();

        using var response = await GetAsync(client, Surveys, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task トークンは_URL_クエリ_cookie_Basic_では受けない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var token = await IssueAsync(host, await SeedAsync(host, AdminRole.Administrator), "surveys.read");
        using var client = host.CreateClient();

        using var query = await GetAsync(client, $"{Surveys}?access_token={token}&token={token}", null);
        using var cookie = await SendAsync(
            client, HttpMethod.Get, Surveys, null, request => request.Headers.Add("Cookie", $"q.admin={token}"));
        using var basic = await SendAsync(
            client, HttpMethod.Get, Surveys, null,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token));
        using var twoHeaders = await SendAsync(
            client, HttpMethod.Get, Surveys, token,
            request => request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}"));

        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, basic.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, twoHeaders.StatusCode);
    }

    // ---- 403: スコープ・許可宣言 --------------------------------------------------------

    [Fact]
    public async Task スコープが足りない口は_403()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var token = await IssueAsync(host, await SeedAsync(host, AdminRole.Administrator), "surveys.read");
        using var client = host.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, Missing(), token, body: new { });
        using var publish = await SendAsync(client, HttpMethod.Post, Missing("/publish"), token, body: new { });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
    }

    [Fact]
    public async Task 持ち主の役割が降格すると_トークンもその場で弱くなる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.SurveyAdministrator);
        var token = await IssueAsync(host, owner, "surveys.read", "surveys.publish");
        using var client = host.CreateClient();

        using var before = await SendAsync(client, HttpMethod.Post, Missing("/publish"), token, body: new { });
        AssertPassedAuthentication(before);

        await host.Services.GetRequiredService<IAdminUserStore>().SetRoleAsync(owner.Id, AdminRoleMap.ToName(AdminRole.Editor));

        using var after = await SendAsync(client, HttpMethod.Post, Missing("/publish"), token, body: new { });
        using var read = await GetAsync(client, Surveys, token);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task 許可宣言の無い口は_全スコープのトークンでも入れない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var owner = await SeedAsync(host, AdminRole.Administrator);
        var token = await IssueAsync(host, owner, "surveys.read", "surveys.write", "surveys.publish");
        using var client = host.CreateClient();

        var denied = new (HttpMethod Method, string Url)[]
        {
            // アンケートの群でも、宣言の無い口（完全削除・アーカイブ・復元・作成・複製・テスト回答の削除・設定）
            (HttpMethod.Post, Missing("/delete")),
            (HttpMethod.Post, Missing("/archive")),
            (HttpMethod.Post, Missing("/restore")),
            (HttpMethod.Post, Missing("/duplicate")),
            (HttpMethod.Post, Missing("/revert-to-draft")),
            (HttpMethod.Put, Missing("/settings")),
            (HttpMethod.Put, Missing("/site-id")),
            (HttpMethod.Post, Missing("/site-settings/sync")),
            (HttpMethod.Post, Missing("/test-responses/delete")),
            (HttpMethod.Get, Missing("/test-responses")),
            (HttpMethod.Post, Surveys),
            (HttpMethod.Get, $"{Surveys}/embed-options"),
            // 人・設定・ルックアップ・監査・メンテナンス
            (HttpMethod.Get, "/api/admin/users"),
            (HttpMethod.Post, $"/api/admin/users/{owner.Id}/role"),
            (HttpMethod.Get, "/api/admin/audit-logs"),
            // **トークンでトークンは作れない・見えない・失効できない**
            (HttpMethod.Get, "/api/admin/me/api-tokens"),
            (HttpMethod.Post, "/api/admin/me/api-tokens"),
            (HttpMethod.Get, $"/api/admin/users/{owner.Id}/api-tokens"),
        };

        foreach (var (method, url) in denied)
        {
            // **認可は口の処理より前**。存在しないアンケートでも 403（404 にならない）
            using var response = await SendAsync(client, method, url, token, body: method == HttpMethod.Get ? null : new { });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} は {response.StatusCode}");
        }
    }

    // ---- cookie との関係 -----------------------------------------------------------------

    [Fact]
    public async Task cookieだけでは今までどおり_Bearerがあればcookieは見ない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var admin = await SeedAsync(host, AdminRole.Administrator);
        using var cookieClient = await CookieClientAsync(host, admin);
        var readOnly = await IssueAsync(host, admin, "surveys.read");

        // cookie だけ: 今までどおり（トークンの管理の口も、人の口も開く）
        using var cookieOnly = await GetAsync(cookieClient, "/api/admin/users", null);
        Assert.Equal(HttpStatusCode.OK, cookieOnly.StatusCode);

        // **Bearer が偽なら、有効な cookie があっても 401**（cookie へ戻らない）
        using var forged = await GetAsync(cookieClient, Surveys, AdminApiTokenSecret.Create().Plain);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // **Bearer が有効でも、権限は cookie（特権管理者）のものにならない**
        using var users = await GetAsync(cookieClient, "/api/admin/users", readOnly);
        using var tokens = await GetAsync(cookieClient, "/api/admin/me/api-tokens", readOnly);
        using var put = await SendAsync(cookieClient, HttpMethod.Put, Missing(), readOnly, body: new { });
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tokens.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        // **cookie が壊れていても、Bearer が有効なら通る**（cookie を評価しない）
        using var broken = host.CreateClient();
        using var withBrokenCookie = await SendAsync(
            broken, HttpMethod.Get, Surveys, readOnly, request => request.Headers.Add("Cookie", "q.admin=broken"));
        Assert.Equal(HttpStatusCode.OK, withBrokenCookie.StatusCode);
    }

    [Fact]
    public async Task 公開の回答画面は_Bearerを持ち込んでも影響を受けない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        using var client = host.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/ready", AdminApiTokenSecret.Create().Plain);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- CSRF: 除外は足さない ---------------------------------------------------------------

    [Fact]
    public async Task Origin付きのBearerは403のまま_ブラウザ以外の要求は通る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var token = await IssueAsync(host, await SeedAsync(host, AdminRole.SurveyAdministrator), "surveys.write", "surveys.publish");
        using var client = host.CreateClient();

        using var crossOrigin = await SendAsync(
            client, HttpMethod.Put, Missing(), token,
            request => request.Headers.Add("Origin", "https://evil.example"), new { });
        using var crossSite = await SendAsync(
            client, HttpMethod.Post, Missing("/publish"), token,
            request => request.Headers.Add("Sec-Fetch-Site", "cross-site"), new { });
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Contains(AdminCsrfGuard.RejectedCode, await crossOrigin.Content.ReadAsStringAsync());

        // curl・CI（Sec-Fetch-Site も Origin も付けない）は、ガードを今のまま通る
        using var script = await SendAsync(client, HttpMethod.Put, Missing(), token, body: new { });
        AssertPassedAuthentication(script);
    }
}
