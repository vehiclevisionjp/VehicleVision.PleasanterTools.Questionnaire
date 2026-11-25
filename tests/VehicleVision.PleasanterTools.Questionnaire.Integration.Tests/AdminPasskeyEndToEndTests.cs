using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Core.Web.Testing;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// パスキーのうち、Core の契約（<see cref="PasskeyContractTests"/>）では見ない、本アプリ固有の取り決めを確かめる（Issue #1082）。
/// </summary>
public class AdminPasskeyEndToEndTests
{
    private static readonly Dictionary<string, string> Enabled = new()
    {
        ["QUESTIONNAIRE_ADMIN_PASSKEY"] = "true",
        ["QUESTIONNAIRE_ADMIN_PASSKEY_RPID"] = "localhost",
        ["QUESTIONNAIRE_ADMIN_PASSKEY_ORIGINS"] = "https://localhost",
    };

    // ---- GET /api/admin/session の passkeyEnabled ----

    [Fact]
    public async Task passkeyEnabledは既定で偽で_未認証と認証済みの両方へ返す()
    {
        await using var host = await AdminContractHost.StartAsync();
        var admin = await host.SeedAdminAsync(withTotp: false);

        using var anonymous = host.CreateClient();
        Assert.False(await PasskeyEnabledAsync(anonymous));

        using var signedIn = host.CreateClient();
        using var login = await host.LoginAsync(signedIn, admin);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(await PasskeyEnabledAsync(signedIn));
    }

    [Fact]
    public async Task passkeyEnabledは有効にすると未認証と認証済みの両方で真になり_設定の中身は返さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: Enabled);
        var admin = await host.SeedAdminAsync(withTotp: false);

        using var anonymous = host.CreateClient();
        Assert.True(await PasskeyEnabledAsync(anonymous));
        using var raw = await anonymous.GetAsync("/api/admin/session");
        var text = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain("localhost", text, StringComparison.OrdinalIgnoreCase);

        using var signedIn = host.CreateClient();
        using var login = await host.LoginAsync(signedIn, admin);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(await PasskeyEnabledAsync(signedIn));
    }

    [Fact]
    public async Task 無効の間はパスキーの口が404で_ログイン画面のタブも出さない()
    {
        await using var host = await AdminContractHost.StartAsync();

        using var client = host.CreateClient();
        using var begin = await client.PostAsJsonAsync("/api/admin/login/passkey/begin", new { });
        using var complete = await client.PostAsJsonAsync("/api/admin/login/passkey/complete", new { credential = new { } });
        using var list = await client.GetAsync("/api/admin/me/passkeys");
        Assert.All([begin, complete, list], response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    // ---- 端末の一覧の loginMethod ----

    [Fact]
    public async Task 端末の一覧のloginMethodはパスキーで入ったセッションだけがPasskeyになる()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var admin = await host.SeedAdminAsync(new());
        var authenticator = new VirtualPasskeyAuthenticator();

        using var password = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(password, admin.LoginId, admin.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        await RegisterAsync(host, password, admin, authenticator);

        using var passkey = host.CreateClient();
        using (var login = await PasskeyLoginAsync(host, passkey, authenticator))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // パスキーで入った端末から見ると、2 つの端末が並び、いまの端末だけが Passkey
        var sessions = await SessionsAsync(passkey);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("Passkey", Assert.Single(sessions, session => session.Current).LoginMethod);
        Assert.Equal("Password", Assert.Single(sessions, session => !session.Current).LoginMethod);
    }

    // ---- 監査 ----

    [Fact]
    public async Task 監査はパスキーの経路を二重に残さず_失敗の理由を区別し_応答には出さない()
    {
        await using var host = await AdminPasskeyContractHost.StartAsync();
        var admin = await host.SeedAdminAsync(new());
        var registered = new VirtualPasskeyAuthenticator();
        var stranger = new VirtualPasskeyAuthenticator();

        using var owner = host.CreateClient();
        using (var login = await host.PasswordLoginAsync(owner, admin.LoginId, admin.Password))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        await RegisterAsync(host, owner, admin, registered);

        // 登録していない認証器（資格情報が無い）と、署名が合わない応答は、応答では見分けがつかない
        using var anonymous = host.CreateClient();
        using var unknown = await PasskeyLoginAsync(host, anonymous, stranger);
        using var broken = await PasskeyLoginAsync(host, anonymous, registered, new() { BreakSignature = true });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, broken.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await broken.Content.ReadAsStringAsync());

        // 期限切れのチャレンジも、ログへは区別して残る
        var options = await LoginOptionsAsync(host, anonymous);
        await host.AdvanceTimeAsync(TimeSpan.FromMinutes(6));
        using var expired = await anonymous.PostAsJsonAsync(
            "/api/admin/login/passkey/complete",
            new { credential = JsonNode.Parse(registered.Get(options, host.Origin)) });
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var failures = (await host.AuditAsync())
            .Where(entry => entry.Action == "Passkey.SignIn.Failed")
            .Select(entry => entry.DetailJson ?? string.Empty)
            .ToList();
        Assert.Contains(failures, detail => detail.Contains("unknown-credential", StringComparison.Ordinal));
        Assert.Contains(failures, detail => detail.Contains("bad-signature", StringComparison.Ordinal));
        Assert.Contains(failures, detail => detail.Contains("challenge", StringComparison.Ordinal));

        // 管理操作の監査フィルタが、同じ経路を二重に残さない（Core のサービスが明示して書く 5 つだけ）
        var all = await host.AllAuditActionsAsync();
        Assert.DoesNotContain(all, action => action.Contains("passkey", StringComparison.OrdinalIgnoreCase)
            && action.StartsWith("POST ", StringComparison.Ordinal));
    }

    // ---- 起動時の検証 ----

    [Theory]
    [InlineData("QUESTIONNAIRE_ADMIN_PASSKEY_RPID")]
    [InlineData("QUESTIONNAIRE_ADMIN_PASSKEY_ORIGINS")]
    public async Task 有効なのにRPIDかOriginsが足りなければ_足りない設定名を挙げて起動を失敗させる(string missing)
    {
        var settings = new Dictionary<string, string>(Enabled);
        settings.Remove(missing);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => AdminContractHost.StartAsync(settings: settings));

        Assert.Contains(missing, Describe(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 無効のときはRPIDとOriginsが無くても起動できる()
    {
        await using var host = await AdminContractHost.StartAsync(settings: new Dictionary<string, string>
        {
            ["QUESTIONNAIRE_ADMIN_PASSKEY"] = "false",
        });

        using var client = host.CreateClient();
        Assert.False(await PasskeyEnabledAsync(client));
    }

    // ---- 道具 ----

    private static string Describe(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private static async Task<bool> PasskeyEnabledAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("passkeyEnabled").GetBoolean();
    }

    private sealed record SessionRow(bool Current, string? LoginMethod);

    private static async Task<List<SessionRow>> SessionsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/admin/me/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.GetProperty("sessions").EnumerateArray().Select(session => new SessionRow(
            session.GetProperty("current").GetBoolean(),
            session.GetProperty("loginMethod").GetString()))];
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

    private static async Task<string> LoginOptionsAsync(AdminPasskeyContractHost host, HttpClient client)
    {
        _ = host;
        using var begin = await client.PostAsJsonAsync("/api/admin/login/passkey/begin", new { });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        return await begin.Content.ReadAsStringAsync();
    }

    private static async Task<HttpResponseMessage> PasskeyLoginAsync(
        AdminPasskeyContractHost host,
        HttpClient client,
        VirtualPasskeyAuthenticator authenticator,
        VirtualPasskeyAuthenticator.Tamper? tamper = null)
    {
        var options = await LoginOptionsAsync(host, client);
        return await client.PostAsJsonAsync(
            "/api/admin/login/passkey/complete",
            new { credential = JsonNode.Parse(authenticator.Get(options, host.Origin, tamper)) });
    }
}
