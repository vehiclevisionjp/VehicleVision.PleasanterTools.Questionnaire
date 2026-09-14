using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>SAML のログインを、検証用 IdP（Keycloak）との往復で確かめる（Issue #190）。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_SAML_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 起動はこう。
/// </para>
/// <code>
/// DEV_SAML_ENABLED=true docker compose --profile sqlserver --profile saml up -d --wait
/// </code>
/// <para>
/// **これまで手順書と手作業しか無かった**（<c>tools/saml-idp/roundtrip.py</c> を人が叩く）。
/// **機械で回らない確認は、いずれ回らなくなる**ので、同じ往復を試験にした。
/// </para>
/// <para>
/// ⚠️ **SAML は HTTPS でしか動かない。** 途中を預ける cookie が
/// <c>SameSite=None; Secure</c> なので、平文の口では往復が完成しない。
/// **当て先は <c>https://localhost:8443</c>。**
/// </para>
/// <para>
/// ⚠️ **ここで確かめられるのはプロトコルの往復まで。**
/// Google Workspace の管理コンソールの手順・属性の名前・制約は、
/// これでは裏が取れない（<c>_documents/SAML認証-運用手順書.md</c>）。
/// </para>
/// </remarks>
public partial class SamlEndToEndTests
{
    private const string Password = "long-enough-password";

    /// <summary>IdP に用意してある利用者。**本アプリにも同じログイン ID を作る。**</summary>
    private const string KnownUser = "admin@example.jp";

    /// <summary>IdP には居るが、**本アプリには居ない**利用者。</summary>
    private const string StrangerUser = "stranger@example.jp";

    private const string IdpPassword = "idp-test-password";

    /// <summary>本アプリの DB。</summary>
    /// <remarks>
    /// **接続先を環境変数で差し替えられる。** 同じ機械で別の検証環境が 11433 を
    /// 使っていることがあり、**その 1 台だけのために試験を落とさない。**
    /// </remarks>
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_E2E_CONNECTIONSTRING")
        ?? "Server=localhost,11433;Database=Questionnaire;UID=sa;PWD=Questionnaire#Test1;TrustServerCertificate=True";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_SAML_INTEGRATION") == "1";

    /// <summary>⚠️ **https の口。** SAML は平文では通らない。</summary>
    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_HTTPS_BASE_URL") ?? "https://localhost:8443";

    /// <summary>途中を預ける cookie の名前。</summary>
    private const string SamlCookieName = "q.admin.saml";

    [GeneratedRegex("<form id=\"kc-form-login\"[^>]*action=\"([^\"]+)\"")]
    private static partial Regex LoginFormAction();

    [GeneratedRegex("name=\"SAMLResponse\" value=\"([^\"]+)\"")]
    private static partial Regex SamlResponseValue();

    [GeneratedRegex("action=\"([^\"]+)\"")]
    private static partial Regex FormAction();

    [GeneratedRegex("name=\"RelayState\" value=\"([^\"]*)\"")]
    private static partial Regex RelayStateValue();

    /// <summary>自己署名の証明書を通す入れ物を作る。</summary>
    /// <remarks>
    /// ⚠️ **検証環境だけ。** 本番の設定に持ち込まないこと（compose.yaml の devcert と同じ但し書き）。
    /// **cookie の入れ物は 1 つ**（ブラウザ 1 つ分）。
    /// </remarks>
    private static HttpClient CreateClient(CookieContainer cookies, bool followRedirects) =>
        new(new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = followRedirects,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = new Uri(BaseUrl),
        };

    /// <summary>本アプリ側の管理者を作り直す。</summary>
    /// <remarks>
    /// **ログイン ID を IdP の利用者に合わせる。** 突き合わせはこれで行う
    /// （<c>_documents/SAML認証-運用手順書.md</c>）。
    /// </remarks>
    private static async Task<string> ResetAdminAsync(string? loginId)
    {
        await using (var connection = new DbConnectionFactory(
            DatabaseProvider.SqlServer, ConnectionString).Create())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM [AdminRecoveryCodes]");
            await connection.ExecuteAsync("DELETE FROM [AdminUsers]");
        }

        if (loginId is null)
        {
            return string.Empty;
        }

        var cookies = new CookieContainer();
        using var http = CreateClient(cookies, followRedirects: true);

        using var setup = await http.PostAsJsonAsync(
            "/api/admin/setup", new { loginId, password = Password });
        setup.EnsureSuccessStatusCode();

        // ⚠️ **2 要素の設定を決め打ちにしない**（Issue #174）。
        // **共有鍵を返す。** SAML で入ったあと、2 要素を出すのに要る
        var enrollment = await AdminTwoFactorE2E.CompleteIfRequiredAsync(
            http, await AdminTwoFactorE2E.NextOfAsync(setup));

        return enrollment.Secret;
    }

    /// <summary>SP → IdP → SP の往復を 1 本通す。</summary>
    /// <returns>SP の受け口が返した飛び先。</returns>
    private static async Task<(string Location, CookieContainer Cookies)> RoundTripAsync(
        string user, string password)
    {
        var cookies = new CookieContainer();
        using var stopper = CreateClient(cookies, followRedirects: false);
        using var follower = CreateClient(cookies, followRedirects: true);

        // ---- 1. SP へログインを頼む（IdP へ飛ばされる）
        using var start = await stopper.GetAsync("/api/admin/saml/login?returnUrl=/admin");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        var idpUrl = start.Headers.Location!.ToString();
        Assert.Contains("SAMLRequest=", idpUrl, StringComparison.Ordinal);

        // **途中を預ける cookie が出ていること。** これが無いと戻ってきたときに繋がらない
        Assert.Contains(
            cookies.GetAllCookies().Cast<Cookie>(),
            cookie => cookie.Name == SamlCookieName);

        // ---- 2. IdP のログイン画面
        var loginHtml = await follower.GetStringAsync(idpUrl);
        var loginUrl = LoginFormAction().Match(loginHtml).Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, loginUrl);

        // ---- 3. 資格情報を出す（SAMLResponse を積んだ form が返る）
        using var authenticated = await follower.PostAsync(
            loginUrl,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = user,
                ["password"] = password,
                ["credentialId"] = string.Empty,
            }));

        var postHtml = await authenticated.Content.ReadAsStringAsync();
        var samlResponse = SamlResponseValue().Match(postHtml);
        Assert.True(samlResponse.Success, "IdP が SAMLResponse を返さなかった");

        var acs = FormAction().Match(postHtml).Groups[1].Value.Replace("&amp;", "&", StringComparison.Ordinal);
        var relay = RelayStateValue().Match(postHtml);

        var fields = new Dictionary<string, string> { ["SAMLResponse"] = samlResponse.Groups[1].Value };
        if (relay.Success)
        {
            fields["RelayState"] = relay.Groups[1].Value;
        }

        // ---- 4. 応答を SP の受け口へ渡す
        using var assertion = await stopper.PostAsync(acs, new FormUrlEncodedContent(fields));

        return (assertion.Headers.Location?.ToString() ?? string.Empty, cookies);
    }

    /// <summary>2 要素が要るなら出しておく。</summary>
    /// <remarks>**必須でない構成なら何もしない。**</remarks>
    private static async Task CompleteSecondFactorIfNeededAsync(
        CookieContainer cookies, string secret)
    {
        var session = await SessionAsync(cookies);
        if (session!["authenticated"]!.GetValue<bool>() || secret.Length == 0)
        {
            return;
        }

        using var http = CreateClient(cookies, followRedirects: true);
        using var totp = await http.PostAsJsonAsync(
            "/api/admin/login/totp", new { code = await NextCodeAsync(secret) });
        totp.EnsureSuccessStatusCode();
    }

    /// <summary>前に使ったものとは違う 2 要素のコードを待つ。</summary>
    /// <remarks>
    /// ⚠️ **同じコードは使い回せない**（30 秒の枠が変わるまで同じ値になる）。
    /// **試験の都合ではなく、実装が正しく弾いている**ことの裏返し。
    /// </remarks>
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

    /// <summary>今の状態を読む。</summary>
    private static async Task<JsonNode?> SessionAsync(CookieContainer cookies)
    {
        using var http = CreateClient(cookies, followRedirects: true);
        var body = await http.GetStringAsync("/api/admin/session");
        return JsonNode.Parse(body);
    }

    [Fact]
    public async Task 本アプリに居る人はIdP経由で入れる()
    {
        if (!Enabled)
        {
            return;
        }

        var secret = await ResetAdminAsync(KnownUser);

        var (location, cookies) = await RoundTripAsync(KnownUser, IdpPassword);

        // **返ってきたら管理画面へ送る**（returnUrl で頼んだ先）
        Assert.Contains("/admin", location, StringComparison.Ordinal);

        var session = await SessionAsync(cookies);

        // ⚠️ **2 要素が必須なら、SAML で来ても省略しない。**
        // IdP が「本人である」と言っただけで、こちら側の 2 要素は満たしていない
        if (!session!["authenticated"]!.GetValue<bool>())
        {
            Assert.True(session["pending"]!.GetValue<bool>(), "途中状態にすらなっていない");
            Assert.Equal(KnownUser, session["pendingLoginId"]!.GetValue<string>());
            Assert.NotEqual(string.Empty, secret);

            using var http = CreateClient(cookies, followRedirects: true);

            // ⚠️ **登録で使ったコードは、そのまま使い回せない**（実測）。
            // 登録から数秒しか経っていないと同じ 30 秒の枠に入るので、
            // **枠が変わるまで待ってから出す。** 人が手で打つ分には起きない事情
            using var totp = await http.PostAsJsonAsync(
                "/api/admin/login/totp", new { code = await NextCodeAsync(secret) });
            Assert.True(
                totp.IsSuccessStatusCode,
                $"2 要素を出せなかった: {totp.StatusCode} {await totp.Content.ReadAsStringAsync()}");

            session = await SessionAsync(cookies);
        }

        Assert.True(session!["authenticated"]!.GetValue<bool>());
        Assert.Equal(KnownUser, session["loginId"]!.GetValue<string>());
    }

    [Fact]
    public async Task 二要素が必須ならSAMLでも省略しない()
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **IdP で認証が通っても、こちら側の 2 要素は別。**
        // ここを緩めると「IdP さえ通れば入れる」ことになる
        await ResetAdminAsync(KnownUser);

        var (_, cookies) = await RoundTripAsync(KnownUser, IdpPassword);
        var session = await SessionAsync(cookies);

        // **2 要素が任意・無効の設定なら、この試験は何も言わない**
        // （compose の既定は必須。DEV_ADMIN_TWOFACTOR で切り替わる）
        if (session!["authenticated"]!.GetValue<bool>())
        {
            return;
        }

        Assert.True(session["pending"]!.GetValue<bool>());
        Assert.Equal(KnownUser, session["pendingLoginId"]!.GetValue<string>());
    }

    [Fact]
    public async Task 本アプリに居ない人は拒絶される()
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **IdP で認証が通っても、本アプリの管理者でなければ入れない**
        // （既定は Reject。_documents/SAML認証-運用手順書.md）
        _ = await ResetAdminAsync(KnownUser);

        var (location, cookies) = await RoundTripAsync(StrangerUser, IdpPassword);

        // **理由は画面へ符号で渡す。** 入れたかどうかは session が正
        Assert.Contains("samlError", location, StringComparison.Ordinal);

        var session = await SessionAsync(cookies);
        Assert.False(session!["authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 途中を預けるcookieを出してからIdPへ送る()
    {
        if (!Enabled)
        {
            return;
        }

        // **この cookie が落ちると、戻ってきた応答を結び付けられない。**
        // ⚠️ **`SameSite=None; Secure` なので https でしか戻らない**
        _ = await ResetAdminAsync(KnownUser);

        var cookies = new CookieContainer();
        using var http = CreateClient(cookies, followRedirects: false);

        using var start = await http.GetAsync("/api/admin/saml/login?returnUrl=/admin");

        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        var saml = cookies.GetAllCookies().Cast<Cookie>().Single(cookie => cookie.Name == SamlCookieName);
        Assert.True(saml.Secure, "途中を預ける cookie に Secure が付いていない");
        Assert.True(saml.HttpOnly, "途中を預ける cookie に HttpOnly が付いていない");
    }

    [Fact]
    public async Task 単一ログアウトでIdP側も落とす()
    {
        if (!Enabled)
        {
            return;
        }

        // ⚠️ **こちらの cookie だけ消すと、IdP のセッションが残る。**
        // 釦を押し直すだけで入り直せてしまい、共用の端末では
        // **ログアウトしたつもりで座席を明け渡す**ことになる（Issue #191）
        var secret = await ResetAdminAsync(KnownUser);
        var (_, cookies) = await RoundTripAsync(KnownUser, IdpPassword);
        await CompleteSecondFactorIfNeededAsync(cookies, secret);

        // **入れていることを確かめてから落とす**
        var before = await SessionAsync(cookies);
        Assert.True(before!["authenticated"]!.GetValue<bool>());

        using (var http = CreateClient(cookies, followRedirects: true))
        {
            // **SP 起点の単一ログアウト。** IdP まで往復して戻ってくる
            using var loggedOut = await http.GetAsync("/api/admin/saml/logout");
            loggedOut.EnsureSuccessStatusCode();
        }

        // **こちらは落ちている**
        var after = await SessionAsync(cookies);
        Assert.False(after!["authenticated"]!.GetValue<bool>());

        // **IdP 側も落ちている。** 落ちていなければ、ログインを頼んだ時点で
        // 画面を出さずにそのまま通してしまう（＝ログイン画面が出る＝落ちている）
        using var stopper = CreateClient(cookies, followRedirects: false);
        using var again = await stopper.GetAsync("/api/admin/saml/login?returnUrl=/admin");
        var idpUrl = again.Headers.Location!.ToString();

        using var idp = CreateClient(new CookieContainer(), followRedirects: true);
        var html = await idp.GetStringAsync(idpUrl);

        Assert.True(
            LoginFormAction().IsMatch(html),
            "IdP 側のセッションが残っていて、ログイン画面が出なかった");
    }

    [Fact]
    public async Task 単一ログアウトの受け口は署名のない要求を通さない()
    {
        if (!Enabled)
        {
            return;
        }

        // **落とされること自体は害が小さい**（入られるより軽い）が、
        // **IdP のふりをした要求に「成功」を返さない**こと
        var cookies = new CookieContainer();
        using var http = CreateClient(cookies, followRedirects: false);

        using var response = await http.GetAsync("/api/admin/saml/slo?SAMLRequest=" + "not-a-request");

        // **管理画面へ戻すだけ。** IdP へ応答を返さない
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain(
            "SAMLResponse",
            response.Headers.Location?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 応答が無ければ受け口は断る()
    {
        if (!Enabled)
        {
            return;
        }

        // **空の要求で入れてしまわないこと**
        var cookies = new CookieContainer();
        using var http = CreateClient(cookies, followRedirects: false);

        using var response = await http.PostAsync(
            "/api/admin/saml/acs",
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);

        var session = await SessionAsync(cookies);
        Assert.False(session!["authenticated"]!.GetValue<bool>());
    }
}
