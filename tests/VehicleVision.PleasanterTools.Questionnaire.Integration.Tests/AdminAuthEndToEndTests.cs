using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者のログインを、動いているアプリへ HTTP で当てて確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動は <c>docker compose --profile sqlserver up -d --wait</c>。
/// </para>
/// <para>
/// **部品の試験では見えない所を見る。** cookie の往復、2 つの認証方式の切り替え、
/// 途中状態で操作させないこと、認証を通っていない要求の扱いは、
/// 実際にミドルウェアを通してみないと分からない。
/// </para>
/// <para>
/// **レート制限そのものはここでは試さない。** 送信元 IP で枠を切っているため、
/// 枠を使い切る試験を入れると同じ IP から動く他の試験を巻き添えにする。
/// 検証環境では <c>QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN</c> で緩めてある。
/// </para>
/// </remarks>
public class AdminAuthEndToEndTests
{
    private const string Password = "long-enough-password";

    /// <summary>アプリが繋いでいる DB。**片付けのために直接触る。**</summary>
    private const string ConnectionString =
        "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081";

    /// <summary>管理者を消す。**「まだ 1 人も居ない」から始めたいため。**</summary>
    private static async Task ClearAdministratorsAsync()
    {
        await using var connection = new DbConnectionFactory(
            DatabaseProvider.SqlServer, ConnectionString).Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM [AdminRecoveryCodes]");
        await connection.ExecuteAsync("DELETE FROM [AdminUsers]");
    }

    /// <summary>cookie を保つ HTTP クライアント。**ブラウザと同じように往復させる。**</summary>
    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = new Uri(BaseUrl),
    };

    private static string Code(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    /// <summary>次の時間枠の数字。</summary>
    /// <remarks>
    /// **登録で使った時間枠はそこで使い切る**ので、続けて同じ数字ではログインできない
    /// （盗み見られた数字の二度目を弾く仕組み）。
    /// 照合は前後 1 枠を許すため、**次の枠の数字なら 30 秒待たずに通せる。**
    /// </remarks>
    private static string NextCode(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow.AddSeconds(30));

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync());

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string path, object? body = null) =>
        http.PostAsJsonAsync(path, body ?? new { });

    /// <summary>最初の管理者を作り、2 要素まで登録してログイン済みにする。</summary>
    private static async Task<(string Secret, string[] RecoveryCodes)> EnrollAsync(
        HttpClient http,
        string loginId)
    {
        using (var setup = await PostAsync(http, "/api/admin/setup", new { loginId, password = Password }))
        {
            setup.EnsureSuccessStatusCode();
        }

        string secret;
        using (var begin = await PostAsync(http, "/api/admin/enroll/begin"))
        {
            begin.EnsureSuccessStatusCode();
            secret = (await ReadAsync(begin))!["secret"]!.GetValue<string>();
        }

        using var complete = await PostAsync(
            http, "/api/admin/enroll/complete", new { code = Code(secret) });
        complete.EnsureSuccessStatusCode();

        var codes = (await ReadAsync(complete))!["recoveryCodes"]!
            .AsArray().Select(node => node!.GetValue<string>()).ToArray();

        return (secret, codes);
    }

    [Fact]
    public async Task 最初の管理者を作って二要素を登録するとログインできる()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var http = CreateClient();

        using (var before = await http.GetAsync("/api/admin/session"))
        {
            var body = await ReadAsync(before);
            Assert.True(body!["setupRequired"]!.GetValue<bool>());
            Assert.False(body["authenticated"]!.GetValue<bool>());
        }

        var (_, codes) = await EnrollAsync(http, "admin");

        // **復旧コードを返せるのは登録の時だけ**
        Assert.Equal(10, codes.Length);

        using var after = await http.GetAsync("/api/admin/session");
        var session = await ReadAsync(after);
        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal("admin", session["loginId"]!.GetValue<string>());
        Assert.Equal("Administrator", session["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 二人目は初期設定の入口から作れない()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var http = CreateClient();
        await EnrollAsync(http, "admin");

        // **cookie を持たない相手から叩く**
        using var other = CreateClient();
        using var response = await PostAsync(
            other, "/api/admin/setup", new { loginId = "attacker", password = Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task 合言葉だけでは認証済みにならない()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var setupClient = CreateClient();
        await EnrollAsync(setupClient, "admin");

        using var http = CreateClient();
        using (var login = await PostAsync(
            http, "/api/admin/login", new { loginId = "admin", password = Password }))
        {
            login.EnsureSuccessStatusCode();
            Assert.Equal("totp", (await ReadAsync(login))!["next"]!.GetValue<string>());
        }

        using var session = await http.GetAsync("/api/admin/session");
        var body = await ReadAsync(session);

        // **途中状態であって、認証済みではない**
        Assert.False(body!["authenticated"]!.GetValue<bool>());
        Assert.True(body["pending"]!.GetValue<bool>());
        Assert.Equal("admin", body["pendingLoginId"]!.GetValue<string>());
    }

    [Fact]
    public async Task 使い捨てパスワードで二要素を通せる()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var setupClient = CreateClient();
        var (secret, _) = await EnrollAsync(setupClient, "admin");

        using var http = CreateClient();
        using (var login = await PostAsync(
            http, "/api/admin/login", new { loginId = "admin", password = Password }))
        {
            login.EnsureSuccessStatusCode();
        }

        using (var totp = await PostAsync(http, "/api/admin/login/totp", new { code = NextCode(secret) }))
        {
            totp.EnsureSuccessStatusCode();
            Assert.True((await ReadAsync(totp))!["authenticated"]!.GetValue<bool>());
        }

        using var session = await http.GetAsync("/api/admin/session");
        Assert.True((await ReadAsync(session))!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 端末を失っても復旧コードで入れる()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var setupClient = CreateClient();
        var (_, codes) = await EnrollAsync(setupClient, "admin");

        using var http = CreateClient();
        using (var login = await PostAsync(
            http, "/api/admin/login", new { loginId = "admin", password = Password }))
        {
            login.EnsureSuccessStatusCode();
        }

        using (var recovery = await PostAsync(
            http, "/api/admin/login/recovery", new { code = codes[0] }))
        {
            recovery.EnsureSuccessStatusCode();
        }

        using var session = await http.GetAsync("/api/admin/session");
        Assert.True((await ReadAsync(session))!["authenticated"]!.GetValue<bool>());

        // **使い切り。** 同じコードで入り直せない
        using var again = CreateClient();
        using (var login = await PostAsync(
            again, "/api/admin/login", new { loginId = "admin", password = Password }))
        {
            login.EnsureSuccessStatusCode();
        }

        using var reused = await PostAsync(again, "/api/admin/login/recovery", new { code = codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task 途中状態でなければ二要素の入口を叩けない()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var setupClient = CreateClient();
        var (secret, _) = await EnrollAsync(setupClient, "admin");

        // **合言葉を通していない相手**
        using var http = CreateClient();

        using (var totp = await PostAsync(http, "/api/admin/login/totp", new { code = Code(secret) }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, totp.StatusCode);
        }

        using var enroll = await PostAsync(http, "/api/admin/enroll/begin");
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Fact]
    public async Task ログアウトすると途中状態も消える()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var http = CreateClient();
        var (secret, _) = await EnrollAsync(http, "admin");

        using (var logout = await PostAsync(http, "/api/admin/logout"))
        {
            logout.EnsureSuccessStatusCode();
        }

        using (var session = await http.GetAsync("/api/admin/session"))
        {
            var body = await ReadAsync(session);
            Assert.False(body!["authenticated"]!.GetValue<bool>());
            // **途中状態も残さない。** 残すと 2 要素から再開できてしまう
            Assert.False(body["pending"]!.GetValue<bool>());
        }

        // 2 要素だけでは入れない
        using var totp = await PostAsync(http, "/api/admin/login/totp", new { code = Code(secret) });
        Assert.Equal(HttpStatusCode.Unauthorized, totp.StatusCode);
    }

    [Fact]
    public async Task 合言葉が違えば通らず段階も区別されない()
    {
        if (!Enabled)
        {
            return;
        }

        await ClearAdministratorsAsync();
        using var setupClient = CreateClient();
        await EnrollAsync(setupClient, "admin");

        using var http = CreateClient();

        using var wrong = await PostAsync(
            http, "/api/admin/login", new { loginId = "admin", password = "wrong-password" });
        using var missing = await PostAsync(
            http, "/api/admin/login", new { loginId = "nobody", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        // **文言まで同じにする。** 違えば利用者名の総当たりに使える
        Assert.Equal(
            (await ReadAsync(wrong))!["message"]!.GetValue<string>(),
            (await ReadAsync(missing))!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task 管理画面の応答は保存させない()
    {
        if (!Enabled)
        {
            return;
        }

        using var http = CreateClient();
        using var response = await http.GetAsync("/api/admin/session");

        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.NoStore);
    }
}
