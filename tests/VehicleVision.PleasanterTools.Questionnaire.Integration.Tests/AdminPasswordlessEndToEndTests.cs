using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Core.Web.Testing;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 「パスワード設定済み」の印（Issue #1086）を、本アプリの組み立てで確かめる。
/// </summary>
/// <remarks>
/// <para>
/// 1 つ目は、<b>最後のパスキーの削除</b>（Core の P5）が、印を使って判定されること。
/// パスワード管理者・JIT 管理者（パスワードを持たない）・招待中の管理者の 3 通り。
/// Core の契約試験は招待中の 1 通りだけを見るので、印の分をここで足す。
/// </para>
/// <para>
/// 2 つ目は、印を切り替える口（<c>POST /api/admin/users/{id}/passwordless</c>）の権限・自分への扱い・監査・一覧。
/// </para>
/// </remarks>
public class AdminPasswordlessEndToEndTests
{
    private static readonly PasskeySeed Operator = new() { Permissions = new HashSet<string> { "users.resetPasskeys" } };

    // ---- 最後のパスキーの削除 ----

    [Fact]
    public async Task パスワード管理者は最後のパスキーを消せる()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (client, admin, passkeyId) = await ArrangeWithOnePasskeyAsync(host);

        using var removed = await RemoveAsync(client, passkeyId, admin);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(await PasskeyIdsAsync(client));
    }

    [Fact]
    public async Task パスワードを持たないJIT管理者は最後のパスキーを消せず_ほかの方法が無いので締め出されない()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (client, admin, passkeyId) = await ArrangeWithOnePasskeyAsync(host);
        // JIT で作られた管理者の状態（本アプリの印が「未設定」。このホストでは SAML・Pleasanter は無効）
        await host.SetPasswordSetAsync(admin, false);

        using var refused = await RemoveAsync(client, passkeyId, admin);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("passkeys.lastSignInMethod", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(await PasskeyIdsAsync(client));
    }

    [Fact]
    public async Task 招待中の管理者は最後のパスキーを消せない()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (client, admin, passkeyId) = await ArrangeWithOnePasskeyAsync(host);
        await host.SetPasswordUsableAsync(admin, false);

        using var refused = await RemoveAsync(client, passkeyId, admin);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Single(await PasskeyIdsAsync(client));
    }

    [Fact]
    public async Task 印を設定済みへ直すと最後のパスキーを消せる()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (client, admin, passkeyId) = await ArrangeWithOnePasskeyAsync(host);
        await host.SetPasswordSetAsync(admin, false);
        using (var refused = await RemoveAsync(client, passkeyId, admin))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        // 管理者（ユーザ管理者）が、画面の切り替えと同じ口で戻す
        var (operatorClient, _) = await LoggedInOperatorAsync(host);
        var adminId = await host.UserIdAsync(admin);
        using (var restored = await operatorClient.PostAsJsonAsync(
            $"/api/admin/users/{adminId}/passwordless", new { passwordless = false }))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        using var removed = await RemoveAsync(client, passkeyId, admin);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
    }

    // ---- 切り替えの口 ----

    [Fact]
    public async Task 切り替えは一覧のpasswordSetへ出て_監査に誰がどちらへかが残る()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var target = await host.SeedAdminAsync(new());
        var targetId = await host.UserIdAsync(target);
        var (operatorClient, operatorAdmin) = await LoggedInOperatorAsync(host);

        Assert.True((await UsersAsync(operatorClient))[targetId].GetProperty("passwordSet").GetBoolean());

        using (var set = await operatorClient.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/passwordless", new { passwordless = true }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            using var body = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("passwordless").GetBoolean());
        }

        Assert.False((await UsersAsync(operatorClient))[targetId].GetProperty("passwordSet").GetBoolean());

        // 相手のパスワードでのログインは、そのまま通る（印はログインの成否を変えない）
        using (var targetClient = host.CreateClient())
        using (var login = await host.PasswordLoginAsync(targetClient, target.LoginId, target.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        var audit = Assert.Single(await host.AdminAuditAsync("POST /api/admin/users/{adminUserId}/passwordless"));
        Assert.True(audit.Succeeded);
        Assert.Equal("AdminUser", audit.TargetType);
        Assert.Equal(targetId.ToString(), audit.TargetId, ignoreCase: true);
        Assert.Equal(operatorAdmin.LoginId, audit.ActorLoginId);
        Assert.Contains("\"passwordless\":\"true\"", audit.DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ユーザー管理の権限が無ければ切り替えられない()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var target = await host.SeedAdminAsync(new());
        var targetId = await host.UserIdAsync(target);
        var editor = await host.SeedAdminAsync(new());
        using var editorClient = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(editorClient, editor.LoginId, editor.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using var denied = await editorClient.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/passwordless", new { passwordless = true });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task 自分をパスワードありへ戻す操作は拒み_パスワードなしにする向きは通し_居ない管理者は404()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (operatorClient, operatorAdmin) = await LoggedInOperatorAsync(host);
        var ownId = await host.UserIdAsync(operatorAdmin);

        using (var toSet = await operatorClient.PostAsJsonAsync(
            $"/api/admin/users/{ownId}/passwordless", new { passwordless = false }))
        {
            Assert.Equal(HttpStatusCode.Conflict, toSet.StatusCode);
        }

        using (var toPasswordless = await operatorClient.PostAsJsonAsync(
            $"/api/admin/users/{ownId}/passwordless", new { passwordless = true }))
        {
            Assert.Equal(HttpStatusCode.OK, toPasswordless.StatusCode);
        }

        Assert.False((await UsersAsync(operatorClient))[ownId].GetProperty("passwordSet").GetBoolean());

        using var missing = await operatorClient.PostAsJsonAsync(
            $"/api/admin/users/{Guid.NewGuid()}/passwordless", new { passwordless = true });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- 道具 ----

    /// <summary>ユーザ管理者（<c>users.write</c> を持つ）でログインした client。</summary>
    private static async Task<(HttpClient Client, SeededAdmin Admin)> LoggedInOperatorAsync(AdminPasskeyContractHost host)
    {
        var admin = await host.SeedAdminAsync(Operator);
        var client = host.CreateClient();
        using var login = await host.PasswordLoginAsync(client, admin.LoginId, admin.Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, admin);
    }

    /// <summary>ログイン済みの管理者と、その人が持つパスキー 1 件（最後のログイン方法になり得る）を用意する。</summary>
    private static async Task<(HttpClient Client, SeededAdmin Admin, string PasskeyId)> ArrangeWithOnePasskeyAsync(
        AdminPasskeyContractHost host)
    {
        var admin = await host.SeedAdminAsync(new());
        var client = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(client, admin.LoginId, admin.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        var authenticator = new VirtualPasskeyAuthenticator();
        using var begin = await client.PostAsJsonAsync("/api/admin/me/passkeys/begin", new { password = admin.Password });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var attestation = authenticator.Create(await begin.Content.ReadAsStringAsync(), host.Origin);
        using var complete = await client.PostAsJsonAsync(
            "/api/admin/me/passkeys/complete", new { credential = JsonNode.Parse(attestation), name = "試験用" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        return (client, admin, Assert.Single(await PasskeyIdsAsync(client)));
    }

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, string passkeyId, SeededAdmin admin) =>
        client.PostAsJsonAsync($"/api/admin/me/passkeys/{passkeyId}/remove", new { password = admin.Password });

    private static async Task<List<string>> PasskeyIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/me/passkeys");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("passkeys").EnumerateArray().Select(row => row.GetProperty("id").GetString()!)];
    }

    private static async Task<Dictionary<Guid, JsonElement>> UsersAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("users").EnumerateArray()
            .ToDictionary(user => user.GetProperty("adminUserId").GetGuid(), user => user.Clone());
    }
}
