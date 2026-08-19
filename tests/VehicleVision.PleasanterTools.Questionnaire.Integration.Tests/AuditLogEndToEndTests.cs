using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理操作が本当に記録されることを、動いているアプリへ当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **部品の試験では、入口に本当に掛かっているかが分からない。**
/// 記録の仕組みを作っても、入口へ繋ぎ忘れていれば 1 行も残らない。
/// </para>
/// </remarks>
public class AuditLogEndToEndTests
{
    private const string Password = "long-enough-password";

    /// <summary>アプリが繋いでいる DB。**確かめるために直接読む。**</summary>
    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    private sealed record LogRow(string Action, int? StatusCode, Guid? AdminUserId, string? DetailJson);

    [Fact]
    public async Task 合言葉が違うログインも記録に残る()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAsync().ConfigureAwait(true);

        using var http = CreateClient();
        using var response = await http.PostAsJsonAsync(
            "/api/admin/login", new { loginId = "居ない人", password = "でたらめ" }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var rows = await ReadLogsAsync().ConfigureAwait(true);
        var entry = Assert.Single(rows, row => row.Action.EndsWith("/login", StringComparison.Ordinal));

        // **誰なのかは分からない。それでも試みは残る**
        Assert.Null(entry.AdminUserId);
        Assert.Equal(401, entry.StatusCode);

        // **どの利用者が狙われているかが分からないと対処できない**
        Assert.Contains("居ない人", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);

        // **合言葉は入らない**
        Assert.DoesNotContain("でたらめ", entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 断られた管理操作も記録に残る()
    {
        if (!Enabled)
        {
            return;
        }

        var (http, adminUserId) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (http)
        {
            await ClearAsync().ConfigureAwait(true);

            // **自分は止められない**（誰も入れなくなる）
            using var response = await http
                .PostAsJsonAsync($"/api/admin/users/{adminUserId}/disable", new { })
                .ConfigureAwait(true);

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

            var rows = await ReadLogsAsync().ConfigureAwait(true);
            var entry = Assert.Single(
                rows, row => row.Action.EndsWith("/disable", StringComparison.Ordinal));

            Assert.Equal(adminUserId, entry.AdminUserId);
            Assert.NotEqual(200, entry.StatusCode);
            Assert.Contains(adminUserId.ToString(), entry.DetailJson ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 一覧を開いただけでは記録が増えない()
    {
        if (!Enabled)
        {
            return;
        }

        var (http, _) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (http)
        {
            await ClearAsync().ConfigureAwait(true);

            using var response = await http.GetAsync("/api/admin/users").ConfigureAwait(true);
            response.EnsureSuccessStatusCode();

            // **読み取りで行が増えると、変えた操作が埋もれる**
            Assert.Empty(await ReadLogsAsync().ConfigureAwait(true));
        }
    }

    [Fact]
    public async Task 管理者は記録を名前付きで読める()
    {
        if (!Enabled)
        {
            return;
        }

        var (http, adminUserId) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (http)
        {
            await ClearAsync().ConfigureAwait(true);

            // 記録が 1 件できる操作をする（自分は止められないので断られる）
            using (var refused = await http
                .PostAsJsonAsync($"/api/admin/users/{adminUserId}/disable", new { })
                .ConfigureAwait(true))
            {
                Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
            }

            using var response = await http.GetAsync("/api/admin/audit-logs").ConfigureAwait(true);
            response.EnsureSuccessStatusCode();

            var body = JsonNode.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            var entry = Assert.Single(body!["entries"]!.AsArray())!;

            // **GUID のままでは読めない。** 誰がやったかは名前で見せる
            Assert.Equal("admin", entry["adminLoginId"]!.GetValue<string>());
            Assert.EndsWith("/disable", entry["action"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.NotEqual(200, entry["statusCode"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task 記録を読んでも記録は増えない()
    {
        if (!Enabled)
        {
            return;
        }

        var (http, _) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (http)
        {
            await ClearAsync().ConfigureAwait(true);

            using (var first = await http.GetAsync("/api/admin/audit-logs").ConfigureAwait(true))
            {
                first.EnsureSuccessStatusCode();
            }

            // **一覧を開くたびに行が増えると、変えた操作が埋もれる**
            Assert.Empty(await ReadLogsAsync().ConfigureAwait(true));
        }
    }

    [Fact]
    public async Task 断られた操作だけ絞れる()
    {
        if (!Enabled)
        {
            return;
        }

        var (http, adminUserId) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (http)
        {
            await ClearAsync().ConfigureAwait(true);

            // 通る操作（自分の言語）と、断られる操作（自分を止める）
            using (var ok = await http
                .PutAsJsonAsync("/api/admin/me/language", new { language = "ja" })
                .ConfigureAwait(true))
            {
                ok.EnsureSuccessStatusCode();
            }

            using (var refused = await http
                .PostAsJsonAsync($"/api/admin/users/{adminUserId}/disable", new { })
                .ConfigureAwait(true))
            {
                Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
            }

            using var response = await http
                .GetAsync("/api/admin/audit-logs?failedOnly=true").ConfigureAwait(true);
            response.EnsureSuccessStatusCode();

            var entries = JsonNode.Parse(
                await response.Content.ReadAsStringAsync().ConfigureAwait(true))!["entries"]!.AsArray();

            var entry = Assert.Single(entries)!;
            Assert.EndsWith("/disable", entry["action"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Editorは記録を読めない()
    {
        if (!Enabled)
        {
            return;
        }

        var (admin, _) = await SignedInAdministratorAsync().ConfigureAwait(true);
        using (admin)
        {
            Guid invitedId;
            string token;
            using (var invited = await admin
                .PostAsJsonAsync("/api/admin/users", new { loginId = "editor", role = "Editor" })
                .ConfigureAwait(true))
            {
                invited.EnsureSuccessStatusCode();
                var body = JsonNode.Parse(
                    await invited.Content.ReadAsStringAsync().ConfigureAwait(true));
                invitedId = body!["adminUserId"]!.GetValue<Guid>();
                token = body["invitationToken"]!.GetValue<string>();
            }

            Assert.NotEqual(Guid.Empty, invitedId);

            using var editor = CreateClient();
            using (var accept = await editor.PostAsJsonAsync(
                "/api/admin/invitations/accept",
                new { token, password = "editor-long-password" }).ConfigureAwait(true))
            {
                accept.EnsureSuccessStatusCode();
            }

            await EnrollTotpAsync(editor).ConfigureAwait(true);

            // **誰が何をしたかは、Editor へ見せる情報ではない。**
            // 画面から入口を隠すだけでは守りにならないので、サーバでも断る
            using var response = await editor.GetAsync("/api/admin/audit-logs").ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ---- 道具 ---------------------------------------------------------------

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
    };

    private static async Task<(HttpClient Http, Guid AdminUserId)> SignedInAdministratorAsync()
    {
        await ClearAdministratorsAsync().ConfigureAwait(false);

        var http = CreateClient();
        using (var setup = await http.PostAsJsonAsync(
            "/api/admin/setup", new { loginId = "admin", password = Password }).ConfigureAwait(false))
        {
            setup.EnsureSuccessStatusCode();
        }

        await EnrollTotpAsync(http).ConfigureAwait(false);

        await using var connection = Connect();
        await connection.OpenAsync().ConfigureAwait(false);
        var adminUserId = await connection
            .QuerySingleAsync<Guid>("SELECT TOP 1 [AdminUserId] FROM [AdminUsers]").ConfigureAwait(false);

        return (http, adminUserId);
    }

    private static System.Data.Common.DbConnection Connect() =>
        new DbConnectionFactory(DatabaseProvider.SqlServer, ConnectionString).Create();

    /// <summary>2 要素を登録してログイン済みにする。</summary>
    private static async Task EnrollTotpAsync(HttpClient http)
    {
        string secret;
        using (var begin = await http.PostAsJsonAsync(
            "/api/admin/enroll/begin", new { }).ConfigureAwait(false))
        {
            begin.EnsureSuccessStatusCode();
            secret = JsonNode.Parse(
                await begin.Content.ReadAsStringAsync().ConfigureAwait(false))!["secret"]!
                .GetValue<string>();
        }

        using var complete = await http.PostAsJsonAsync(
            "/api/admin/enroll/complete",
            new { code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp() })
            .ConfigureAwait(false);

        complete.EnsureSuccessStatusCode();
    }

    private static async Task ClearAsync()
    {
        await using var connection = Connect();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM [AuditLogs]").ConfigureAwait(false);
    }

    private static async Task ClearAdministratorsAsync()
    {
        await using var connection = Connect();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM [AdminInvitations]").ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM [AdminRecoveryCodes]").ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM [AdminUsers]").ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LogRow>> ReadLogsAsync()
    {
        await using var connection = Connect();
        await connection.OpenAsync().ConfigureAwait(false);

        var rows = await connection.QueryAsync<LogRow>(
            "SELECT [Action], [StatusCode], [AdminUserId], [DetailJson] "
            + "FROM [AuditLogs] ORDER BY [OccurredAt] DESC").ConfigureAwait(false);

        return rows.ToList();
    }
}
