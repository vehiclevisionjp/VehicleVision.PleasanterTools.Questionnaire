using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Core.Web.Testing;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理者の一覧の「パスキーをすべて削除する（復旧）」画面が使う口を、本アプリの組み立てで確かめる（Issue #1087）。
/// </summary>
/// <remarks>
/// 復旧の口そのもの（権限・自分の拒否・全セッションの失効・監査）は Core の契約試験（<see cref="PasskeyContractTests"/>）が見る。
/// ここで見るのは、**画面が件数を出すための `GET /api/admin/users` の `passkeyCount`**（本アプリ固有）と、
/// 画面が通す一連の流れ（件数 → 復旧 → 件数 0 と監査）。
/// </remarks>
public class AdminPasskeyResetEndToEndTests
{
    private static readonly PasskeySeed Operator = new() { Permissions = new HashSet<string> { "users.resetPasskeys" } };

    [Fact]
    public async Task 一覧のpasskeyCountは復旧の権限を持つ相手にだけ_件数だけを返す()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (operatorClient, _, targetId) = await ArrangeAsync(host, registered: 2);

        var rows = await UsersAsync(operatorClient);

        Assert.Equal(2, rows[targetId].GetProperty("passkeyCount").GetInt32());
        // 件数だけ。名前・資格情報 ID は一覧に出さない
        var text = rows[targetId].GetRawText();
        Assert.DoesNotContain("credentialId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name\":\"試験用", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一覧のpasskeyCountはパスキーが無効の間は返さない()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (operatorClient, _, targetId) = await ArrangeAsync(host, registered: 1);

        await host.SetPasskeyEnabledAsync(false);
        var rows = await UsersAsync(operatorClient);

        Assert.False(rows[targetId].TryGetProperty("passkeyCount", out _));
    }

    [Fact]
    public async Task 復旧すると件数が0になり_by_adminの監査が資格情報ごとに残り_自分と権限なしは拒まれる()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var (operatorClient, ownId, targetId) = await ArrangeAsync(host, registered: 2);

        // 権限の無い管理者（Editor）は 403（画面で隠すだけにしない）
        var editor = await host.SeedAdminAsync(new());
        using var editorClient = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(editorClient, editor.LoginId, editor.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using (var forbidden = await editorClient.PostAsJsonAsync($"/api/admin/users/{targetId}/passkeys/reset", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // 自分自身は対象にできない（通常の削除でできる）
        using (var self = await operatorClient.PostAsJsonAsync($"/api/admin/users/{ownId}/passkeys/reset", new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
            Assert.Contains("users.selfNotAllowed", await self.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var reset = await operatorClient.PostAsJsonAsync($"/api/admin/users/{targetId}/passkeys/reset", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var body = JsonDocument.Parse(await reset.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("removed").GetInt32());

        Assert.Equal(0, (await UsersAsync(operatorClient))[targetId].GetProperty("passkeyCount").GetInt32());

        var removed = (await host.AuditAsync())
            .Where(entry => entry.Action == "Passkey.Removed" && (entry.DetailJson ?? string.Empty).Contains("\"admin\"", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, removed.Count);
    }

    // ---- 道具 ----

    /// <summary>復旧の権限を持つ管理者（ログイン済み）と、パスキーを <paramref name="registered"/> 件持つ対象を用意する。</summary>
    private static async Task<(HttpClient Operator, Guid OperatorId, Guid TargetId)> ArrangeAsync(
        AdminPasskeyContractHost host, int registered)
    {
        var actor = await host.SeedAdminAsync(Operator);
        var operatorClient = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(operatorClient, actor.LoginId, actor.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        var target = await host.SeedAdminAsync(Operator);
        var targetId = await host.UserIdAsync(target);
        using var targetClient = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(targetClient, target.LoginId, target.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        for (var index = 0; index < registered; index++)
        {
            await RegisterAsync(host, targetClient, target, new VirtualPasskeyAuthenticator());
        }

        return (operatorClient, await host.UserIdAsync(actor), targetId);
    }

    private static async Task<Dictionary<Guid, JsonElement>> UsersAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("users").EnumerateArray()
            .ToDictionary(user => user.GetProperty("adminUserId").GetGuid(), user => user.Clone());
    }

    private static async Task RegisterAsync(
        AdminPasskeyContractHost host, HttpClient client, SeededAdmin admin, VirtualPasskeyAuthenticator authenticator)
    {
        using var begin = await client.PostAsJsonAsync("/api/admin/me/passkeys/begin", new { password = admin.Password });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var attestation = authenticator.Create(await begin.Content.ReadAsStringAsync(), host.Origin);
        using var complete = await client.PostAsJsonAsync(
            "/api/admin/me/passkeys/complete", new { credential = JsonNode.Parse(attestation), name = "試験用" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }
}
