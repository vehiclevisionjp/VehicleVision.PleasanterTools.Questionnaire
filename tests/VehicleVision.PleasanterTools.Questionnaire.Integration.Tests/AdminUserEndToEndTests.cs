using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者の管理 API を、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **部品の試験では見えない所を見る。** 役割による切り分け（Editor が他人を触れないこと）と、
/// **止めた管理者がその場で追い出されること**は、
/// 認証のミドルウェアを通してみないと分からない。
/// </para>
/// <para>
/// **1 つの試験でログインの枠を 5 回ほど使う。**
/// 検証環境では <c>QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN</c> を緩めておくこと。
/// </para>
/// </remarks>
public class AdminUserEndToEndTests
{
    private const string Password = "long-enough-password";
    private const string EditorPassword = "editor-long-password";

    /// <summary>アプリが繋いでいる DB。**片付けのために直接触る。**</summary>
    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private static async Task ClearAdministratorsAsync()
    {
        await using var connection = new DbConnectionFactory(
            DatabaseProvider.SqlServer, ConnectionString).Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM [AdminInvitations]");
        await connection.ExecuteAsync("DELETE FROM [AdminRecoveryCodes]");
        await connection.ExecuteAsync("DELETE FROM [AdminUsers]");
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
    };

    private static string Code(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object? body = null) =>
        http.PostAsJsonAsync(path, body ?? new { });

    /// <summary>2 要素まで登録して、ログイン済みにする。</summary>
    private static async Task EnrollTotpAsync(HttpClient http)
    {
        string secret;
        using (var begin = await PostAsync(http, "/api/admin/enroll/begin"))
        {
            begin.EnsureSuccessStatusCode();
            secret = (await ReadAsync(begin))!["secret"]!.GetValue<string>();
        }

        using var complete = await PostAsync(
            http, "/api/admin/enroll/complete", new { code = Code(secret) });
        complete.EnsureSuccessStatusCode();
    }

    /// <summary>最初の管理者を作り、ログイン済みの client を返す。</summary>
    private static async Task<HttpClient> SignedInAdministratorAsync()
    {
        await ClearAdministratorsAsync();

        var http = CreateClient();
        using (var setup = await PostAsync(
            http, "/api/admin/setup", new { loginId = "admin", password = Password }))
        {
            setup.EnsureSuccessStatusCode();
        }

        await EnrollTotpAsync(http);
        return http;
    }

    /// <summary>招待を出す。**トークンを受け取れるのはこの時だけ。**</summary>
    private static async Task<(Guid AdminUserId, string Token)> InviteAsync(
        HttpClient administrator,
        string loginId,
        string role)
    {
        using var response = await PostAsync(
            administrator, "/api/admin/users", new { loginId, role });
        response.EnsureSuccessStatusCode();

        var body = await ReadAsync(response);
        return (
            body!["adminUserId"]!.GetValue<Guid>(),
            body["invitationToken"]!.GetValue<string>());
    }

    /// <summary>招待を受け取り、2 要素まで登録してログイン済みにする。</summary>
    private static async Task<HttpClient> AcceptAsync(string token, string password)
    {
        var http = CreateClient();
        using (var accept = await PostAsync(
            http, "/api/admin/invitations/accept", new { token, password }))
        {
            accept.EnsureSuccessStatusCode();
            Assert.Equal("enroll", (await ReadAsync(accept))!["next"]!.GetValue<string>());
        }

        await EnrollTotpAsync(http);
        return http;
    }

    [Fact]
    public async Task 管理者は二人目を招待できる()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (adminUserId, _) = await InviteAsync(admin, "editor", "Editor");

        using var list = await admin.GetAsync("/api/admin/users");
        list.EnsureSuccessStatusCode();

        var invited = (await ReadAsync(list))!["users"]!.AsArray()
            .Single(user => user!["adminUserId"]!.GetValue<Guid>() == adminUserId)!;

        Assert.Equal("Editor", invited["role"]!.GetValue<string>());
        // **まだ受け取っていない。** 一度も入っていないことが分かる
        Assert.True(invited["invitationPending"]!.GetValue<bool>());
        Assert.Null(invited["lastLoginAt"]);
    }

    [Fact]
    public async Task 招待を受け取ると自分で決めた合言葉で入れる()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");

        using var editor = await AcceptAsync(token, EditorPassword);

        using var session = await editor.GetAsync("/api/admin/session");
        var body = await ReadAsync(session);
        Assert.True(body!["authenticated"]!.GetValue<bool>());
        Assert.Equal("Editor", body["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 使い終えた招待は二度使えない()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");
        using var editor = await AcceptAsync(token, EditorPassword);

        using var other = CreateClient();
        using var again = await PostAsync(
            other, "/api/admin/invitations/accept", new { token, password = "yet-another-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task Editorは他人を触れない()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");
        using var editor = await AcceptAsync(token, EditorPassword);

        // **一覧も追加も通さない**
        using (var list = await editor.GetAsync("/api/admin/users"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        }

        using var invite = await PostAsync(
            editor, "/api/admin/users", new { loginId = "another", role = "Administrator" });
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
    }

    [Fact]
    public async Task Editorも自分の合言葉は変えられる()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");
        using var editor = await AcceptAsync(token, EditorPassword);

        using var change = await PostAsync(
            editor,
            "/api/admin/me/password",
            new { currentPassword = EditorPassword, newPassword = "changed-long-password" });

        change.EnsureSuccessStatusCode();
    }

    /// <summary>自分自身は止められないし、降格もできない。</summary>
    /// <remarks>
    /// **HTTP からは、この 2 つが「最後の管理者を残す」条件も兼ねている。**
    /// 操作できるのは有効な Administrator だけなので、
    /// 自分以外を止めても**操作した本人が必ず残る**。
    /// SQL 側の条件（<c>TryDisableAsync</c> / <c>TrySetRoleAsync</c>）は、
    /// 画面以外から呼ばれたときと同時実行のための備えであり、
    /// そちらは <c>AdminUserManagementTests</c> で見る。
    /// </remarks>
    [Fact]
    public async Task 自分自身は止められないし降格もできない()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");
        using var editor = await AcceptAsync(token, EditorPassword);

        var me = await MyIdAsync(admin);

        using (var self = await PostAsync(admin, $"/api/admin/users/{me}/disable"))
        {
            Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        }

        // **Editor しか残らなければ、誰も管理者を足せなくなる**
        using var demote = await PostAsync(
            admin, $"/api/admin/users/{me}/role", new { role = "Editor" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
    }

    [Fact]
    public async Task 止めた管理者はその場で追い出される()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (editorId, token) = await InviteAsync(admin, "editor", "Editor");
        using var editor = await AcceptAsync(token, EditorPassword);

        using (var session = await editor.GetAsync("/api/admin/session"))
        {
            Assert.True((await ReadAsync(session))!["authenticated"]!.GetValue<bool>());
        }

        using (var disable = await PostAsync(admin, $"/api/admin/users/{editorId}/disable"))
        {
            disable.EnsureSuccessStatusCode();
        }

        // **cookie は 8 時間有効だが、止めた瞬間に効かなくなる**
        using var after = await editor.GetAsync("/api/admin/session");
        Assert.False((await ReadAsync(after))!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 降格した管理者は他人を触れなくなる()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (secondId, token) = await InviteAsync(admin, "admin2", "Administrator");
        using var second = await AcceptAsync(token, EditorPassword);

        using (var list = await second.GetAsync("/api/admin/users"))
        {
            list.EnsureSuccessStatusCode();
        }

        using (var demote = await PostAsync(
            admin, $"/api/admin/users/{secondId}/role", new { role = "Editor" }))
        {
            demote.EnsureSuccessStatusCode();
        }

        // **古い cookie に載った役割で操作を続けさせない**
        using var after = await second.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task 短い合言葉では招待を受け取れない()
    {
        if (!Enabled)
        {
            return;
        }

        using var admin = await SignedInAdministratorAsync();
        var (_, token) = await InviteAsync(admin, "editor", "Editor");

        using var http = CreateClient();
        using var accept = await PostAsync(
            http, "/api/admin/invitations/accept", new { token, password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
    }

    [Fact]
    public async Task 認証を通っていなければ一覧は見られない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        using var list = await http.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    /// <summary>自分の <c>AdminUserId</c> を、一覧から引く。</summary>
    private static async Task<Guid> MyIdAsync(HttpClient administrator)
    {
        string loginId;
        using (var session = await administrator.GetAsync("/api/admin/session"))
        {
            loginId = (await ReadAsync(session))!["loginId"]!.GetValue<string>();
        }

        using var list = await administrator.GetAsync("/api/admin/users");
        return (await ReadAsync(list))!["users"]!.AsArray()
            .Single(user => string.Equals(
                user!["loginId"]!.GetValue<string>(), loginId, StringComparison.Ordinal))!
            ["adminUserId"]!.GetValue<Guid>();
    }
}
