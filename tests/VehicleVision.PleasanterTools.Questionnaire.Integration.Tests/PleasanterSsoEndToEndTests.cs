using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// Pleasanter のログインで管理画面へ入る機能（Issue #464）を、検証環境の Pleasanter との往復で確かめる（Issue #470）。
/// </summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_PLEASANTERSSO_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 本アプリは <c>compose.pleasanter-sso.yaml</c> を重ねて起動し、Pleasanter には
/// <c>tools/pleasanter-testenv/seed/03_sso_users.sql</c> の利用者を入れておくこと。
/// </para>
/// <code>
/// docker compose -f compose.yaml -f compose.pleasanter-sso.yaml --profile sqlserver up -d --wait
/// </code>
/// <para>
/// **ブラウザがすることを HTTP でそのままなぞる。** Pleasanter のログイン画面で
/// ログインし（受け取った cookie を持つ）、その cookie を付けたまま本アプリの確認の入口を呼ぶ。
/// cookie はポートを区別しないので、ブラウザは <c>localhost:8080</c>（Pleasanter）の cookie を
/// <c>localhost:8081</c>（本アプリ）へも送る。<see cref="SharedHostBrowser"/> がそれをする。
/// </para>
/// <para>
/// ⚠️ **本アプリの cookie は Pleasanter へ送らない。** 本番の構成（本アプリを Pleasanter と
/// 同じホストのサブパスに置く）では、本アプリの cookie の Path はサブパスに閉じるため
/// Pleasanter へは届かない（<c>_documents/Pleasanter-SSO-運用手順書.md</c>）。
/// サブパスを使わない検証環境でそのまま送ると、Pleasanter のログアウトが受け取った cookie を
/// 消すため <c>q.admin</c> も巻き添えで消え、再検証を確かめられなくなる。
/// </para>
/// <para>
/// ⚠️ **Pleasanter の 2 要素のコードは DB から読む。** 試験の利用者にはメールアドレスを
/// 登録していないので、Pleasanter はメールを送らず、コードを
/// <c>Users.SecondaryAuthenticationCode</c> へ平文で残すだけになる。
/// </para>
/// </remarks>
public class PleasanterSsoEndToEndTests
{
    private const string AppPassword = "long-enough-password";

    /// <summary>2 要素の無い Pleasanter の利用者。</summary>
    private const string PlainUser = "sso-e2e-plain";

    private const string PlainPassword = "SsoE2e#Plain1";

    /// <summary>メールのワンタイムパスワードを有効にした Pleasanter の利用者。</summary>
    private const string MailUser = "sso-e2e-mail";

    private const string MailPassword = "SsoE2e#Mail1";

    /// <summary>Pleasanter には居るが、**本アプリには作らない**利用者。</summary>
    /// <remarks>
    /// ⚠️ **Administrator を使わない。** 作りたての Administrator は初回のログインで
    /// パスワードの変更を求められ、ログインが最後まで通らない（CI で実際に踏んだ）。
    /// </remarks>
    private const string StrangerUser = "sso-e2e-stranger";

    private const string StrangerPassword = "SsoE2e#Stranger1";

    /// <summary>compose.pleasanter-sso.yaml の再検証の間隔（1 分）。</summary>
    private static readonly TimeSpan RevalidateInterval = TimeSpan.FromMinutes(1);

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_PLEASANTERSSO_INTEGRATION") == "1";

    private static Uri AppBaseUrl => new(
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_BASE_URL") ?? "http://localhost:8081");

    /// <summary>**ブラウザから見た** Pleasanter。本アプリの設定のログイン画面と同じホスト。</summary>
    private static Uri PleasanterBaseUrl => new(
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_PLEASANTER_BASEURL") ?? "http://localhost:8080");

    /// <summary>Pleasanter の DB（2 要素のコードを読むため）。</summary>
    private static string PleasanterConnectionString =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_E2E_PLEASANTER_CONNECTIONSTRING")
            ?? "Server=localhost,11433;Database=Implem.Pleasanter;UID=sa;Password="
                + (Environment.GetEnvironmentVariable("TESTENV_SA_PASSWORD") ?? "Questionnaire#Test1")
                + ";TrustServerCertificate=True";

    [Fact]
    public async Task 二要素の無い利用者はPleasanterのログインで入れる()
    {
        if (!Enabled)
        {
            return;
        }

        var admin = await ResetAdminAsync(PlainUser);
        using var browser = new SharedHostBrowser();

        // **Pleasanter にログインする前は入れない**（画面はこれを見て別窓を開く）
        var before = await browser.CheckAsync();
        Assert.Equal("unauthenticated", before!["status"]!.GetValue<string>());

        var login = await browser.LoginToPleasanterAsync(PlainUser, PlainPassword);
        AssertPleasanterSignedIn(login);

        var checkedAt = DateTimeOffset.UtcNow;
        var after = await browser.CheckAsync();
        Assert.Equal("signedIn", after!["status"]!.GetValue<string>());
        await CompleteAppSecondFactorAsync(browser, after, admin.Secret, checkedAt);

        var session = await browser.SessionAsync();
        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal(PlainUser, session["loginId"]!.GetValue<string>());
        Assert.True(session["viaPleasanterSso"]!.GetValue<bool>(), "Pleasanter のログインで入った印が無い");
    }

    [Fact]
    public async Task メールの2要素を終えるまでは入れない()
    {
        if (!Enabled)
        {
            return;
        }

        var admin = await ResetAdminAsync(MailUser);
        using var browser = new SharedHostBrowser();

        // ---- 1. パスワードだけ。Pleasanter はコードの入力欄を返す
        var passwordOnly = await browser.LoginToPleasanterAsync(MailUser, MailPassword);
        Assert.Contains("SecondaryAuthenticationCode", passwordOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Method\":\"Href\"", passwordOnly, StringComparison.Ordinal);

        // **認証 cookie はまだ出ない**（UserModel.Authenticate は 2 要素の後で Allow する）
        Assert.DoesNotContain(
            browser.PleasanterCookies(),
            cookie => cookie.Name.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal));

        var pending = await browser.CheckAsync();
        Assert.Equal("unauthenticated", pending!["status"]!.GetValue<string>());
        Assert.False((await browser.SessionAsync())!["authenticated"]!.GetValue<bool>());

        // ---- 2. 違うコードでは入れない
        var code = await SecondaryAuthenticationCodeAsync(MailUser);
        var wrongCode = code == "00000000" ? "11111111" : "00000000";
        var rejected = await browser.LoginToPleasanterAsync(MailUser, MailPassword, wrongCode);
        Assert.DoesNotContain("\"Method\":\"Href\"", rejected, StringComparison.Ordinal);
        Assert.Equal("unauthenticated", (await browser.CheckAsync())!["status"]!.GetValue<string>());

        // ---- 3. メールで届くはずのコードを入れると入れる
        var accepted = await browser.LoginToPleasanterAsync(MailUser, MailPassword, code);
        AssertPleasanterSignedIn(accepted);

        var checkedAt = DateTimeOffset.UtcNow;
        var after = await browser.CheckAsync();
        Assert.Equal("signedIn", after!["status"]!.GetValue<string>());
        await CompleteAppSecondFactorAsync(browser, after, admin.Secret, checkedAt);

        var session = await browser.SessionAsync();
        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal(MailUser, session["loginId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pleasanterでログアウトすると再検証で締め出される()
    {
        if (!Enabled)
        {
            return;
        }

        var admin = await ResetAdminAsync(PlainUser);
        using var browser = new SharedHostBrowser();

        AssertPleasanterSignedIn(await browser.LoginToPleasanterAsync(PlainUser, PlainPassword));

        // **再検証の起点は確認の入口を通った時刻**（セッションに載る。2 要素の途中から引き継ぐ）
        var checkedAt = DateTimeOffset.UtcNow;
        var check = await browser.CheckAsync();
        Assert.Equal("signedIn", check!["status"]!.GetValue<string>());
        await CompleteAppSecondFactorAsync(browser, check, admin.Secret, checkedAt);
        Assert.True((await browser.SessionAsync())!["authenticated"]!.GetValue<bool>());

        // ---- Pleasanter でログアウトする（ブラウザは Pleasanter の cookie を消される）
        await browser.LogoutFromPleasanterAsync();
        Assert.DoesNotContain(
            browser.PleasanterCookies(),
            cookie => cookie.Name.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal));

        // ---- 間隔を過ぎるまで待つ。**間隔の中では問い合わせない**（仕様。既定 5 分、ここでは 1 分）
        var wait = checkedAt + RevalidateInterval + TimeSpan.FromSeconds(5) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        // **本アプリの cookie はまだ持っている。** それでも入れないこと
        Assert.Contains(browser.AppCookies(), cookie => cookie.Name == "q.admin");

        var session = await browser.SessionAsync();
        Assert.False(
            session!["authenticated"]!.GetValue<bool>(),
            "Pleasanter でログアウトした後も、本アプリのセッションが残っている");

        // **管理 API も通らない**（セッションは消されている）
        using var surveys = await browser.App.GetAsync("/api/admin/surveys");
        Assert.Equal(HttpStatusCode.Unauthorized, surveys.StatusCode);
    }

    [Fact]
    public async Task 本アプリに居ない人はPleasanterでログインしていても入れない()
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **Pleasanter で認証が通っても、本アプリの管理者でなければ入れない**
        // （compose.pleasanter-sso.yaml は未登録の利用者の扱いを既定の Reject で固定している）
        _ = await ResetAdminAsync(PlainUser);
        using var browser = new SharedHostBrowser();

        AssertPleasanterSignedIn(
            await browser.LoginToPleasanterAsync(StrangerUser, StrangerPassword));

        using var response = await browser.App.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown-user", body!["code"]!.GetValue<string>());
        Assert.False((await browser.SessionAsync())!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 外部設定で決めた項目は設定画面から変えられない()
    {
        if (!Enabled)
        {
            return;
        }

        // **検証環境は外部設定（compose.pleasanter-sso.yaml）で有効にしている。**
        // 画面から無効にしようとしても、外部設定が勝つこと（SAML と同じ）
        var admin = await ResetAdminAsync(PlainUser);
        using var http = CreateAppClient(admin.Cookies);

        var settings = JsonNode.Parse(await http.GetStringAsync("/api/admin/pleasanter-sso/settings"));
        Assert.True(settings!["enabled"]!.GetValue<bool>());
        Assert.True(settings["fixedFields"]!["enabled"]!.GetValue<bool>());
        Assert.True(settings["fixedFields"]!["internalBaseUrl"]!.GetValue<bool>());
        Assert.True(settings["fixedFields"]!["loginUrl"]!.GetValue<bool>());
        Assert.True(settings["fixedFields"]!["revalidateMinutes"]!.GetValue<bool>());
        Assert.True(settings["fixedFields"]!["unknownUser"]!.GetValue<bool>());
        Assert.Equal("Reject", settings["unknownUser"]!.GetValue<string>());

        // **外部設定に無い項目は画面から変えられる**（方式は既定の標準の API）
        Assert.False(settings["fixedFields"]!["method"]!.GetValue<bool>());
        Assert.Equal("StandardApi", settings["method"]!.GetValue<string>());

        using var saved = await http.PutAsJsonAsync("/api/admin/pleasanter-sso/settings", new
        {
            enabled = false,
            internalBaseUrl = "http://example.invalid/",
            loginUrl = "/users/login",
            logoutUrl = string.Empty,
            method = "StandardApi",
            sqlName = settings["sqlName"]!.GetValue<string>(),
            cookieNames = settings["cookieNames"]!.GetValue<string>(),
            unknownUser = settings["unknownUser"]!.GetValue<string>(),
            registerRole = settings["registerRole"]!.GetValue<string>(),
            revalidateMinutes = "30",
            timeoutSeconds = settings["timeoutSeconds"]!.GetValue<string>(),
            buttonLabel = settings["buttonLabel"]!.GetValue<string>(),
        });
        saved.EnsureSuccessStatusCode();

        var after = JsonNode.Parse(await http.GetStringAsync("/api/admin/pleasanter-sso/settings"));
        Assert.True(after!["enabled"]!.GetValue<bool>());
        Assert.Equal(
            settings["internalBaseUrl"]!.GetValue<string>(),
            after["internalBaseUrl"]!.GetValue<string>());
        Assert.Equal(
            settings["loginUrl"]!.GetValue<string>(),
            after["loginUrl"]!.GetValue<string>());
        Assert.Equal(
            settings["revalidateMinutes"]!.GetValue<string>(),
            after["revalidateMinutes"]!.GetValue<string>());

        // **ログイン画面にも有効のまま出る**（URL はブラウザで開くログイン画面だけ）
        var session = JsonNode.Parse(await http.GetStringAsync("/api/admin/session"));
        Assert.True(session!["pleasanterSsoEnabled"]!.GetValue<bool>());
    }

    /// <summary>Pleasanter のログインが最後まで通った応答か。</summary>
    /// <remarks>通ると、画面を移す指示（<c>Href</c>）が返る。2 要素の途中なら入力欄が返る。</remarks>
    private static void AssertPleasanterSignedIn(string response) =>
        Assert.True(
            response.Contains("\"Method\":\"Href\"", StringComparison.Ordinal),
            $"Pleasanter のログインが通らなかった: {response[..Math.Min(response.Length, 300)]}");

    /// <summary>本アプリの管理者を作り直す。**ログイン ID を Pleasanter の利用者に合わせる。**</summary>
    /// <returns>2 要素の共有鍵（登録しなかったときは空）と、初期設定を済ませたセッションの cookie。</returns>
    private static async Task<(string Secret, CookieContainer Cookies)> ResetAdminAsync(string loginId)
    {
        await using (var connection = E2EDatabase.AppFactory().Create())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(E2EDatabase.Sql("DELETE FROM [AdminRecoveryCodes]"));
            await connection.ExecuteAsync(E2EDatabase.Sql("DELETE FROM [AdminUsers]"));
        }

        var cookies = new CookieContainer();
        using var http = CreateAppClient(cookies);

        using var setup = await http.PostAsJsonAsync(
            "/api/admin/setup", new { loginId, password = AppPassword });
        setup.EnsureSuccessStatusCode();

        // ⚠️ **2 要素の設定を決め打ちにしない**（Issue #174）
        var enrollment = await AdminTwoFactorE2E.CompleteIfRequiredAsync(
            http, await AdminTwoFactorE2E.NextOfAsync(setup));

        return (enrollment.Secret, cookies);
    }

    private static HttpClient CreateAppClient(CookieContainer cookies) =>
        new(new HttpClientHandler { CookieContainer = cookies, UseCookies = true })
        {
            BaseAddress = AppBaseUrl,
        };

    /// <summary>確認の入口の応答に従って、本アプリ側の 2 要素を済ませる。</summary>
    /// <remarks>
    /// **Pleasanter で入っても、本アプリで登録済みの 2 要素は省かない**（SAML と同じ）。
    /// 検証環境は 2 要素が必須なので、初期設定で登録した共有鍵のコードを出す。
    /// ⚠️ **登録で使った時間枠のコードは使い回せない**ので、枠が変わるまで待つ。
    /// 待つ間に再検証の間隔（1 分）を越えないよう、<paramref name="checkedAt"/> から測って見張る。
    /// </remarks>
    private static async Task CompleteAppSecondFactorAsync(
        SharedHostBrowser browser,
        JsonNode check,
        string secret,
        DateTimeOffset checkedAt)
    {
        var next = check["next"]?.GetValue<string>() ?? "done";
        switch (next)
        {
            case "done":
                return;

            case "totp":
                Assert.NotEqual(string.Empty, secret);
                using (var totp = await browser.App.PostAsJsonAsync(
                    "/api/admin/login/totp", new { code = await NextCodeAsync(secret) }))
                {
                    Assert.True(
                        totp.IsSuccessStatusCode,
                        $"2 要素を出せなかった: {totp.StatusCode} {await totp.Content.ReadAsStringAsync()}");
                }

                break;

            case "enroll":
                _ = await AdminTwoFactorE2E.CompleteIfRequiredAsync(browser.App, next);
                break;

            default:
                Assert.Fail($"知らない next: {next}");
                break;
        }

        Assert.True(
            DateTimeOffset.UtcNow - checkedAt < RevalidateInterval,
            "2 要素を済ませる間に再検証の間隔を越えた。試験の前提が崩れている");
    }

    /// <summary>前に使ったものとは違う 2 要素のコードを待つ。</summary>
    private static async Task<string> NextCodeAsync(string secret)
    {
        var first = AdminTwoFactorE2E.Code(secret);

        for (var waited = 0; waited < 35; waited++)
        {
            var code = AdminTwoFactorE2E.Code(secret);
            if (code != first)
            {
                return code;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return AdminTwoFactorE2E.Code(secret);
    }

    /// <summary>Pleasanter がメールで送るはずの 2 要素のコードを DB から読む。</summary>
    private static async Task<string> SecondaryAuthenticationCodeAsync(string loginId)
    {
        await using var connection = new DbConnectionFactory(DatabaseProvider.SqlServer, PleasanterConnectionString)
            .Create();
        await connection.OpenAsync();

        var code = await connection.ExecuteScalarAsync<string?>(
            "SELECT [SecondaryAuthenticationCode] FROM [Users] WHERE [LoginId] = @LoginId",
            new { LoginId = loginId });

        Assert.False(string.IsNullOrEmpty(code), "Pleasanter が 2 要素のコードを作っていない");
        return code!;
    }

    /// <summary>
    /// 同じホストで動く Pleasanter と本アプリを行き来するブラウザ 1 つ分。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **Pleasanter の cookie は本アプリへも送る**（ブラウザはポートで cookie を分けない）。
    /// **本アプリの cookie は Pleasanter へ送らない**（本番のサブパス配置と同じ。クラスの注記）。
    /// </para>
    /// <para>
    /// 本アプリの口はリダイレクトしないので、自動のリダイレクトは切ってある。
    /// </para>
    /// </remarks>
    private sealed class SharedHostBrowser : IDisposable
    {
        private readonly CookieContainer pleasanterCookies = new();
        private readonly CookieContainer appCookies = new();

        public SharedHostBrowser()
        {
            Pleasanter = new HttpClient(new HttpClientHandler
            {
                CookieContainer = pleasanterCookies,
                UseCookies = true,
            })
            {
                BaseAddress = PleasanterBaseUrl,
            };

            App = new HttpClient(new SharedCookieHandler(appCookies, pleasanterCookies))
            {
                BaseAddress = AppBaseUrl,
            };
        }

        /// <summary>Pleasanter の画面と API。</summary>
        public HttpClient Pleasanter { get; }

        /// <summary>本アプリ。**Pleasanter の cookie も一緒に送る。**</summary>
        public HttpClient App { get; }

        public IReadOnlyList<Cookie> PleasanterCookies() =>
            pleasanterCookies.GetCookies(PleasanterBaseUrl).Cast<Cookie>().ToList();

        public IReadOnlyList<Cookie> AppCookies() =>
            appCookies.GetCookies(AppBaseUrl).Cast<Cookie>().ToList();

        /// <summary>Pleasanter のログイン画面でログインする（画面の JavaScript と同じ要求）。</summary>
        /// <param name="secondaryAuthenticationCode">2 要素のコード。**パスワードと一緒に送り直す**（画面と同じ）。</param>
        /// <returns>Pleasanter が返した画面への指示（JSON）。</returns>
        public async Task<string> LoginToPleasanterAsync(
            string loginId,
            string password,
            string? secondaryAuthenticationCode = null)
        {
            if (secondaryAuthenticationCode is null)
            {
                // **ログイン画面を一度開く。** 画面が配る cookie（Pleasanter_SessionGuid）を受け取る
                using var page = await Pleasanter.GetAsync("/users/login");
                page.EnsureSuccessStatusCode();
            }

            var fields = new Dictionary<string, string>
            {
                ["Users_LoginId"] = loginId,
                ["Users_Password"] = password,
            };
            if (secondaryAuthenticationCode is not null)
            {
                fields["SecondaryAuthenticationCode"] = secondaryAuthenticationCode;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "/users/authenticate?ReturnUrl=")
            {
                Content = new FormUrlEncodedContent(fields),
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");

            using var response = await Pleasanter.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>Pleasanter のログアウト（画面の「ログアウト」と同じ）。</summary>
        public async Task LogoutFromPleasanterAsync()
        {
            using var response = await Pleasanter.GetAsync("/users/logout");
            response.EnsureSuccessStatusCode();
        }

        /// <summary>本アプリのログイン画面が呼ぶ確認の入口。</summary>
        public async Task<JsonNode?> CheckAsync()
        {
            using var response = await App.PostAsJsonAsync("/api/admin/pleasanter-sso/check", new { });
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"確認の入口が失敗した: {response.StatusCode} {body}");
            return JsonNode.Parse(body);
        }

        public async Task<JsonNode?> SessionAsync() =>
            JsonNode.Parse(await App.GetStringAsync("/api/admin/session"));

        public void Dispose()
        {
            Pleasanter.Dispose();
            App.Dispose();
        }
    }

    /// <summary>本アプリへの要求に、本アプリの cookie と Pleasanter の cookie を載せる。</summary>
    private sealed class SharedCookieHandler(CookieContainer appCookies, CookieContainer pleasanterCookies)
        : DelegatingHandler(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var header = string.Join(
                "; ",
                new[] { appCookies.GetCookieHeader(uri), pleasanterCookies.GetCookieHeader(PleasanterBaseUrl) }
                    .Where(part => part.Length > 0));
            if (header.Length > 0)
            {
                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // **本アプリが出した cookie は本アプリの入れ物にだけしまう**
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    appCookies.SetCookies(uri, setCookie);
                }
            }

            return response;
        }
    }
}
