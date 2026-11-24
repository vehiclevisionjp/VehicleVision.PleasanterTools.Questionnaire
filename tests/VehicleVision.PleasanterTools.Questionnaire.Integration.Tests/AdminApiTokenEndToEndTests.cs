using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理 API の個人トークン（API キー）の発行・一覧・失効を、実アプリの host（SQLite）へ HTTP で当てて確かめる（Issue #1025）。
/// </summary>
/// <remarks>
/// <para>
/// 認証のミドルウェア・認可・監査・SQL を<b>通してみないと分からない所</b>（平文が DB・監査に残らないこと、
/// 役割ごとの可否、パスワード変更での失効、本文なしの POST の束ね方）を見る。
/// <b>SQLite だけ</b>で動く（実機の SQL Server・PostgreSQL・MySQL は別。PR 本文の「未検証」を参照）。
/// </para>
/// </remarks>
public sealed class AdminApiTokenEndToEndTests
{
    private const string Password = "contract-long-password-1";
    private const string Tokens = "/api/admin/me/api-tokens";

    private static readonly IReadOnlyDictionary<string, string> Enabled =
        new Dictionary<string, string> { [AdminApiTokenOptions.ModeSetting] = "enabled" };

    private sealed record Account(Guid Id, string LoginId);

    /// <summary>役割を指定して管理者を作る。</summary>
    private static async Task<Account> SeedAsync(AdminContractHost host, AdminRole role, bool withTotp = false)
    {
        var loginId = Guid.NewGuid().ToString("N") + "@example.test";
        var store = host.Services.GetRequiredService<IAdminUserStore>();
        var user = await store.CreateAsync(
            loginId, host.Services.GetRequiredService<PasswordHasher>().Hash(Password), AdminRoleMap.ToName(role), "en");
        Assert.NotNull(user);

        if (withTotp)
        {
            await store.EnableTotpAsync(user.Id, host.Services.GetRequiredService<TotpService>().GenerateSecret(), 0, []);
        }

        return new Account(user.Id, loginId);
    }

    private static async Task<HttpClient> SignedInAsync(AdminContractHost host, Account account)
    {
        var client = host.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/admin/login", new { loginId = account.LoginId, password = Password });
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<(HttpClient Client, Account Account)> NewAsync(AdminContractHost host, AdminRole role)
    {
        var account = await SeedAsync(host, role);
        return (await SignedInAsync(host, account), account);
    }

    private static async Task<JsonDocument> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<(HttpResponseMessage Response, string? Token, Guid? Id)> IssueAsync(
        HttpClient client,
        string[]? scopes = null,
        int? days = null,
        string password = Password,
        string name = "ci")
    {
        var response = await client.PostAsJsonAsync(Tokens, new
        {
            name,
            scopes = scopes ?? ["surveys.read"],
            expiresInDays = days,
            ipAllowList = Array.Empty<string>(),
            password,
        });

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response, null, null);
        }

        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        return (
            response,
            json.RootElement.GetProperty("token").GetString(),
            json.RootElement.GetProperty("apiToken").GetProperty("adminApiTokenId").GetGuid());
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client, string path = Tokens)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonOf(response);
        return json.RootElement.GetProperty("tokens").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<string> DumpTableAsync(AdminContractHost host, string table)
    {
        var factory = host.Services.GetRequiredService<IDbConnectionFactory>();
        await using var connection = factory.Create();
        await connection.OpenAsync();
        var rows = await connection.QueryAsync($"SELECT * FROM \"{table}\"");
        return string.Join('\n', rows.Select(row => string.Join('|', ((IDictionary<string, object?>)row).Values)));
    }

    // ---- 既定は無効 -------------------------------------------------------------------

    [Fact]
    public async Task 機能は既定で無効_発行はできず一覧は空()
    {
        await using var host = await AdminContractHost.StartAsync();
        var (client, _) = await NewAsync(host, AdminRole.Administrator);

        using var state = await client.GetAsync(Tokens);
        using var json = await JsonOf(state);
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(json.RootElement.GetProperty("canIssue").GetBoolean());

        var (response, token, _) = await IssueAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(token);
        Assert.Empty(await ListAsync(client));
    }

    // ---- 発行・一覧・失効 -------------------------------------------------------------

    [Fact]
    public async Task 発行すると平文は発行の応答に1回だけ出て_DBにはハッシュだけが入る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, owner) = await NewAsync(host, AdminRole.Administrator);

        var (response, token, id) = await IssueAsync(client, scopes: ["surveys.read", "surveys.write"], days: 30);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("qat_", token, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);

        // **一覧には平文もハッシュも出ない**
        var listed = Assert.Single(await ListAsync(client));
        Assert.Equal(id, listed.GetProperty("adminApiTokenId").GetGuid());
        Assert.Equal("active", listed.GetProperty("status").GetString());
        Assert.Equal(token![..AdminApiTokenSecret.PrefixLength], listed.GetProperty("prefix").GetString());
        Assert.False(listed.TryGetProperty("token", out _));
        Assert.False(listed.TryGetProperty("tokenHash", out _));
        Assert.DoesNotContain(token, listed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(["surveys.read", "surveys.write"], listed.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray());

        // **DB のどこにも平文が無く、保存されているのは SHA-256 のハッシュ**
        var dump = await DumpTableAsync(host, "AdminApiTokens");
        Assert.DoesNotContain(token, dump, StringComparison.Ordinal);
        Assert.Contains(AdminApiTokenSecret.HashOf(token), dump, StringComparison.Ordinal);
        Assert.Contains(owner.Id.ToString(), dump, StringComparison.OrdinalIgnoreCase);

        // **監査には発行が残り（名前・接頭辞・スコープ）、値は残らない**
        var audit = await DumpTableAsync(host, "Core_AuditLogs");
        Assert.Contains("AdminApiToken", audit, StringComparison.Ordinal);
        Assert.Contains($"POST /api/admin/me/api-tokens", audit, StringComparison.Ordinal);
        Assert.Contains(token[..AdminApiTokenSecret.PrefixLength], audit, StringComparison.Ordinal);
        Assert.DoesNotContain(token, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminApiTokenSecret.HashOf(token), audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 期限を指定しなければ既定の90日で_無期限や上限超えは作れない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);

        var (ok, _, _) = await IssueAsync(client);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var listed = Assert.Single(await ListAsync(client));
        var days = (listed.GetProperty("expiresAt").GetDateTime() - listed.GetProperty("createdAt").GetDateTime()).TotalDays;
        Assert.InRange(days, 89.99, 90.01);

        foreach (var invalid in new int[] { 0, -5, 366, 100000 })
        {
            var (response, token, _) = await IssueAsync(client, days: invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(token);
        }

        Assert.Single(await ListAsync(client));
    }

    [Fact]
    public async Task パスワードが違えば発行できない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);

        var (response, token, _) = await IssueAsync(client, password: "not-the-password-0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(token);
        Assert.Empty(await ListAsync(client));
        Assert.DoesNotContain("not-the-password-0", await DumpTableAsync(host, "Core_AuditLogs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("users.write")]
    [InlineData("users.resetTwoFactor")]
    [InlineData("settings.manage")]
    [InlineData("settings.saml")]
    [InlineData("lookups.manage")]
    [InlineData("surveys.delete")]
    [InlineData("maintenance.manage")]
    [InlineData("audit.read")]
    public async Task 許可リストに無いスコープは特権管理者でも付けられない(string scope)
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);

        var (response, token, _) = await IssueAsync(client, scopes: ["surveys.read", scope]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(token);
        Assert.Empty(await ListAsync(client));
    }

    [Fact]
    public async Task 本人が失効するとその場で失効の状態になり_他人の識別子では失効できない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);
        var (other, _) = await NewAsync(host, AdminRole.Administrator);
        var (_, token, id) = await IssueAsync(client);

        // 他人の識別子は「見つからない」と同じ
        using var stranger = await other.PostAsJsonAsync($"{Tokens}/{id}/revoke", new { });
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal("active", Assert.Single(await ListAsync(client)).GetProperty("status").GetString());

        using var revoke = await client.PostAsJsonAsync($"{Tokens}/{id}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var listed = Assert.Single(await ListAsync(client));
        Assert.Equal("revoked", listed.GetProperty("status").GetString());
        Assert.Equal("owner", listed.GetProperty("revokeReason").GetString());

        var validation = await host.Services.GetRequiredService<AdminApiTokenService>()
            .ValidateAsync(token, IPAddress.Loopback);
        Assert.Equal(AdminApiTokenRejection.Revoked, validation.Rejection);

        // 2 回目は「見つからない」（すでに失効している）
        using var again = await client.PostAsJsonAsync($"{Tokens}/{id}/revoke", new { });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // 失効は監査に残り、値は残らない
        var audit = await DumpTableAsync(host, "Core_AuditLogs");
        Assert.Contains("/revoke", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(token!, audit, StringComparison.Ordinal);
    }

    // ---- 役割ごとの可否 ---------------------------------------------------------------

    [Fact]
    public async Task 編集者は公開スコープを付けられず_読み書きは付けられる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Editor);

        using var state = await client.GetAsync(Tokens);
        using var json = await JsonOf(state);
        Assert.True(json.RootElement.GetProperty("canIssue").GetBoolean());
        Assert.Equal(
            ["surveys.read", "surveys.write"],
            json.RootElement.GetProperty("issuableScopes").EnumerateArray().Select(s => s.GetString()!).ToArray());

        var (denied, none, _) = await IssueAsync(client, scopes: ["surveys.publish"]);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(none);

        var (allowed, token, _) = await IssueAsync(client, scopes: ["surveys.read", "surveys.write"]);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.NotNull(token);
    }

    [Theory]
    [InlineData(AdminRole.UserAdministrator)]
    [InlineData(AdminRole.Auditor)]
    public async Task ユーザ管理者と監査担当は発行できない(AdminRole role)
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, role);

        using var state = await client.GetAsync(Tokens);
        using var json = await JsonOf(state);
        Assert.False(json.RootElement.GetProperty("canIssue").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("issuableScopes").EnumerateArray());

        var (response, token, _) = await IssueAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(token);
    }

    [Fact]
    public async Task 他人のトークンの一覧と失効は_管理者を触れる人だけができる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (editor, editorAccount) = await NewAsync(host, AdminRole.Editor);
        var (userAdmin, _) = await NewAsync(host, AdminRole.UserAdministrator);
        var (otherEditor, _) = await NewAsync(host, AdminRole.Editor);
        var (_, token, id) = await IssueAsync(editor);
        var others = $"/api/admin/users/{editorAccount.Id}/api-tokens";

        // 編集者は他人のトークンを見られず、失効もできない
        using (var denied = await otherEditor.GetAsync(others))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var deniedRevoke = await otherEditor.PostAsJsonAsync($"{others}/{id}/revoke", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedRevoke.StatusCode);
        }

        // ユーザ管理者は見られて、失効できる（発行はできない）
        Assert.Single(await ListAsync(userAdmin, others));
        using var revoke = await userAdmin.PostAsJsonAsync($"{others}/{id}/revoke", new { });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var listed = Assert.Single(await ListAsync(editor));
        Assert.Equal("revoked", listed.GetProperty("status").GetString());
        Assert.Equal("administrator", listed.GetProperty("revokeReason").GetString());
        Assert.DoesNotContain(token!, await DumpTableAsync(host, "Core_AuditLogs"), StringComparison.Ordinal);
    }

    // ---- 自動失効・確認ダイアログの失効 --------------------------------------------------

    [Fact]
    public async Task 持ち主を止めるとトークンは失効する()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (admin, _) = await NewAsync(host, AdminRole.Administrator);
        var (editor, editorAccount) = await NewAsync(host, AdminRole.Editor);
        var (_, token, _) = await IssueAsync(editor);
        var service = host.Services.GetRequiredService<AdminApiTokenService>();
        Assert.True((await service.ValidateAsync(token, IPAddress.Loopback)).Succeeded);

        using var disable = await admin.PostAsJsonAsync($"/api/admin/users/{editorAccount.Id}/disable", new { });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var after = await service.ValidateAsync(token, IPAddress.Loopback);
        Assert.False(after.Succeeded);
        var listed = Assert.Single(await ListAsync(admin, $"/api/admin/users/{editorAccount.Id}/api-tokens"));
        Assert.Equal("ownerDisabled", listed.GetProperty("revokeReason").GetString());
    }

    [Fact]
    public async Task パスワードを変えると既定でAPIキーも失効し_チェックを外せば残る()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);
        var (_, kept, keptId) = await IssueAsync(client, name: "kept");

        // チェックを外す → 残る
        using (var keep = await client.PostAsJsonAsync("/api/admin/me/password", new
        {
            currentPassword = Password,
            newPassword = "second-long-password-2",
            revokeApiTokens = false,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
            using var body = await JsonOf(keep);
            Assert.Equal(0, body.RootElement.GetProperty("revokedApiTokens").GetInt32());
        }

        Assert.Equal("active", Assert.Single(await ListAsync(client)).GetProperty("status").GetString());

        // 指定なし（初期値オン）→ 失効する
        using (var revoke = await client.PostAsJsonAsync("/api/admin/me/password", new
        {
            currentPassword = "second-long-password-2",
            newPassword = "third-long-password-3",
        }))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            using var body = await JsonOf(revoke);
            Assert.Equal(1, body.RootElement.GetProperty("revokedApiTokens").GetInt32());
        }

        var listed = Assert.Single(await ListAsync(client));
        Assert.Equal(keptId, listed.GetProperty("adminApiTokenId").GetGuid());
        Assert.Equal("revoked", listed.GetProperty("status").GetString());
        Assert.Equal("passwordChanged", listed.GetProperty("revokeReason").GetString());

        // 監査には失効した接頭辞が残り、値は残らない
        var audit = await DumpTableAsync(host, "Core_AuditLogs");
        Assert.Contains(kept![..AdminApiTokenSecret.PrefixLength], audit, StringComparison.Ordinal);
        Assert.DoesNotContain(kept, audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 他人の2要素を解除すると既定でその人のAPIキーも失効し_本文で残せる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (admin, _) = await NewAsync(host, AdminRole.Administrator);
        var first = await SeedAsync(host, AdminRole.Editor, withTotp: true);
        var second = await SeedAsync(host, AdminRole.Editor, withTotp: true);

        // 2 要素を登録済みの相手でも、トークンの発行はサービスを通して用意する（ログインに 2 要素が要るため）
        var service = host.Services.GetRequiredService<AdminApiTokenService>();
        var now = DateTimeOffset.UtcNow;
        var (_, firstToken) = await service.IssueAsync(
            first.Id,
            new AdminApiTokenIssueRequest("a", [AdminPermissions.SurveysRead], null, null),
            new AdminApiTokenReauthentication.Relogin(now));
        var (_, secondToken) = await service.IssueAsync(
            second.Id,
            new AdminApiTokenIssueRequest("b", [AdminPermissions.SurveysRead], null, null),
            new AdminApiTokenReauthentication.Relogin(now));
        Assert.NotNull(firstToken);
        Assert.NotNull(secondToken);

        // 本文なし（POST のまま）→ 失効する
        using (var reset = await admin.PostAsync($"/api/admin/users/{first.Id}/totp/reset", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            using var body = await JsonOf(reset);
            Assert.Equal(1, body.RootElement.GetProperty("revokedApiTokens").GetInt32());
        }

        Assert.False((await service.ValidateAsync(firstToken.Token, IPAddress.Loopback)).Succeeded);

        // 本文でチェックを外す → 残る
        using (var keep = await admin.PostAsJsonAsync(
            $"/api/admin/users/{second.Id}/totp/reset", new { revokeApiTokens = false }))
        {
            Assert.Equal(HttpStatusCode.OK, keep.StatusCode);
            using var body = await JsonOf(keep);
            Assert.Equal(0, body.RootElement.GetProperty("revokedApiTokens").GetInt32());
        }

        Assert.True((await service.ValidateAsync(secondToken.Token, IPAddress.Loopback)).Succeeded);
    }

    // ---- トークンでトークンは作れない（#1026 の Bearer 認証が入った後も通し続ける） ------------------

    [Fact]
    public async Task トークンでは管理APIに入れず_トークンでトークンも作れない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var (client, _) = await NewAsync(host, AdminRole.Administrator);
        var (_, token, _) = await IssueAsync(client, scopes: ["surveys.read", "surveys.write", "surveys.publish"]);

        // cookie を持たない別の client が、Bearer だけで叩く。
        // **#1026 で、許可を宣言した口（アンケートの取得など）には入れる。トークンの管理・人・設定の口には、宣言が無いので入れない**
        using var bare = host.CreateClient();
        bare.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var list = await bare.GetAsync("/api/admin/surveys");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var users = await bare.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        using var issue = await bare.PostAsJsonAsync(Tokens, new { name = "x", scopes = new[] { "surveys.read" }, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
        using var mine = await bare.GetAsync(Tokens);
        Assert.Equal(HttpStatusCode.Forbidden, mine.StatusCode);
        using var revoke = await bare.PostAsync($"{Tokens}/{Guid.NewGuid()}/revoke", null);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);

        // 試みの後も、トークンは増えていない
        Assert.Single(await ListAsync(client));
    }
}
