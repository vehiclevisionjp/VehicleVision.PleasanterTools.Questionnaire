using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者ごとの表示言語と、サーバが返す文言の言語を確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **部品の試験では見えない所を見る。** 設定が DB へ残ることと、
/// <c>Accept-Language</c> が応答の文言に効くことは、
/// マイグレーションと入口の両方を通してみないと分からない。
/// </para>
/// </remarks>
public class AdminLanguageEndToEndTests
{
    private const string Password = "long-enough-password";

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

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object? body = null) =>
        http.PostAsJsonAsync(path, body ?? new { });

    private static string Code(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    private static async Task<HttpClient> SignedInAdministratorAsync()
    {
        await ClearAdministratorsAsync();

        var http = CreateClient();
        using (var setup = await PostAsync(
            http, "/api/admin/setup", new { loginId = "admin", password = Password }))
        {
            setup.EnsureSuccessStatusCode();
        }

        string secret;
        using (var begin = await PostAsync(http, "/api/admin/enroll/begin"))
        {
            begin.EnsureSuccessStatusCode();
            secret = (await ReadAsync(begin))!["secret"]!.GetValue<string>();
        }

        using (var complete = await PostAsync(
            http, "/api/admin/enroll/complete", new { code = Code(secret) }))
        {
            complete.EnsureSuccessStatusCode();
        }

        return http;
    }

    [Fact]
    public async Task 選んでいなければ言語はnullで返る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignedInAdministratorAsync();

        using var session = await http.GetAsync("/api/admin/session");
        session.EnsureSuccessStatusCode();

        // **既定値で ja を埋めない。**
        // 埋めると「日本語を選んだ人」と「まだ選んでいない人」を区別できなくなる
        var body = await ReadAsync(session);
        Assert.Null(body!["language"]?.GetValue<string?>());
    }

    [Fact]
    public async Task 選んだ言語は次に開いたときも残る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignedInAdministratorAsync();

        using (var put = await http.PutAsJsonAsync("/api/admin/me/language", new { language = "en" }))
        {
            put.EnsureSuccessStatusCode();
            Assert.Equal("en", (await ReadAsync(put))!["language"]!.GetValue<string>());
        }

        using var session = await http.GetAsync("/api/admin/session");
        session.EnsureSuccessStatusCode();
        Assert.Equal("en", (await ReadAsync(session))!["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task 空を送ると選んでいない状態に戻る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignedInAdministratorAsync();

        using (var put = await http.PutAsJsonAsync("/api/admin/me/language", new { language = "en" }))
        {
            put.EnsureSuccessStatusCode();
        }

        using (var reset = await http.PutAsJsonAsync(
            "/api/admin/me/language", new { language = (string?)null }))
        {
            reset.EnsureSuccessStatusCode();
        }

        using var session = await http.GetAsync("/api/admin/session");
        Assert.Null((await ReadAsync(session))!["language"]?.GetValue<string?>());
    }

    [Fact]
    public async Task 対応していない言語は断る()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = await SignedInAdministratorAsync();

        using var put = await http.PutAsJsonAsync("/api/admin/me/language", new { language = "fr" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task ログインしていなければ言語を変えられない()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var http = CreateClient();

        using var put = await http.PutAsJsonAsync("/api/admin/me/language", new { language = "en" });
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task AcceptLanguageで応答の文言が変わる()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();

        using var http = CreateClient();

        // **パスワードが短いので断られる。** その文言が要求の言語で返ることを見る
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/setup")
        {
            Content = JsonContent.Create(new { loginId = "admin", password = "short" }),
        };
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = (await ReadAsync(response))!["message"]!.GetValue<string>();
        Assert.Contains("characters", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptLanguageが無ければ日本語で返る()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();

        using var http = CreateClient();
        using var response = await PostAsync(
            http, "/api/admin/setup", new { loginId = "admin", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = (await ReadAsync(response))!["message"]!.GetValue<string>();
        Assert.Contains("パスワード", message, StringComparison.Ordinal);
    }
}
