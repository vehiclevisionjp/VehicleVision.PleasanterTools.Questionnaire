using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.IdentityModel.Tokens.Saml2;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// SAML の管理者の API キー発行で、IdP への強制再認証（ForceAuthn）を要求することを、
/// <b>偽の IdP（自己署名の証明書で署名した応答）</b>で実アプリの host（SQLite）へ HTTP で当てて確かめる（Issue #1034。親 #1012）。
/// </summary>
/// <remarks>
/// <para>
/// 本物の IdP は立てない（Keycloak 向けの <c>SamlEndToEndTests</c> とは別。あちらは環境変数で有効にする往復の確認）。
/// ここで確かめるのは、<b>こちらの検証が正しいこと</b>
/// （ForceAuthn を出す・AuthnInstant・InResponseTo・使い捨て・戻り先の固定・CSRF・署名）。
/// ⚠️ <b>実機の IdP が ForceAuthn をどう扱うか</b>（Google Workspace・Entra ID など）は、これでは分からない（PR 本文の「未検証」）。
/// </para>
/// <para>
/// 偽の IdP が応答の AuthnInstant を自由に決められるのは、署名する前にアサーションの文を差し替えているから。
/// 本物の IdP が ForceAuthn を無視したときの「古い AuthnInstant の応答」を作るのに使う。
/// </para>
/// </remarks>
public sealed class SamlReauthEndToEndTests : IAsyncLifetime
{
    private const string SpEntityId = "https://sp.example.test/saml";
    private const string IdpEntityId = "https://idp.example.test/metadata";
    private const string IdpSsoUrl = "https://idp.example.test/sso";
    private const string Acs = "https://localhost/api/admin/saml/acs";
    private const string Tokens = "/api/admin/me/api-tokens";

    private readonly X509Certificate2 idpCertificate = CreateCertificate();
    private AdminContractHost host = null!;

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake-idp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    /// <summary>設定を固定で返す（<c>AdminContractHost</c> は SAML を無効に固定して起動するため、提供元ごと差し替える）。</summary>
    private sealed class FixedSamlOptionsProvider(SamlOptions options) : ISamlOptionsProvider
    {
        private readonly SamlOptionsSnapshot snapshot = new(
            options, new Data.SamlSettingValues(), new HashSet<string>());

        public Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<SamlOptionsSnapshot> SaveAsync(
            Data.SamlSettingValues values, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public async Task InitializeAsync()
    {
        var values = new Dictionary<string, string?>
        {
            [SamlOptions.EnabledKey] = "true",
            [SamlOptions.EntityIdKey] = SpEntityId,
            [SamlOptions.IdpEntityIdKey] = IdpEntityId,
            [SamlOptions.SingleSignOnUrlKey] = IdpSsoUrl,
            [SamlOptions.IdpCertificateKey] = Convert.ToBase64String(idpCertificate.RawData),
            [SamlOptions.UnknownUserKey] = "Reject",
        };
        var options = SamlOptions.FromValues(key => values.GetValueOrDefault(key));

        host = await AdminContractHost.StartAsync(
            services => services.AddSingleton<ISamlOptionsProvider>(new FixedSamlOptionsProvider(options)),
            new Dictionary<string, string> { [AdminApiTokenOptions.ModeSetting] = "enabled" });
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        idpCertificate.Dispose();
    }

    // ---- 偽の IdP -----------------------------------------------------------------------

    private sealed record AuthnRequestFacts(string Id, bool ForceAuthn, string? Destination, string? AcsUrl);

    /// <summary>IdP へのリダイレクト（HTTP-Redirect バインディング）から、出した AuthnRequest を読む。</summary>
    private static AuthnRequestFacts ReadAuthnRequest(string redirectUrl)
    {
        var uri = new Uri(redirectUrl);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(IdpSsoUrl, uri.GetLeftPart(UriPartial.Path));

        using var inflate = new DeflateStream(
            new MemoryStream(Convert.FromBase64String(query["SAMLRequest"]!)), CompressionMode.Decompress);
        var document = new XmlDocument();
        document.Load(inflate);
        var root = document.DocumentElement!;

        return new AuthnRequestFacts(
            root.GetAttribute("ID"),
            root.GetAttribute("ForceAuthn") == "true",
            root.GetAttribute("Destination"),
            root.GetAttribute("AssertionConsumerServiceURL"));
    }

    /// <summary>偽の IdP が出す応答（署名付き）。</summary>
    /// <param name="authnInstant">認証の時刻。<c>null</c> なら「いま」。</param>
    private string SignedResponse(
        string inResponseTo,
        string nameId,
        DateTimeOffset? authnInstant = null,
        X509Certificate2? signer = null)
    {
        var configuration = new Saml2Configuration
        {
            Issuer = IdpEntityId,
            SigningCertificate = signer ?? idpCertificate,
            SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        var response = new Saml2AuthnResponse(configuration)
        {
            Status = Saml2StatusCodes.Success,
            InResponseTo = new Saml2Id(inResponseTo),
            Destination = new Uri(Acs),
            NameId = new Saml2NameIdentifier(nameId, NameIdentifierFormats.Email),
            SessionIndex = "session-" + Guid.NewGuid().ToString("N"),
            ClaimsIdentity = new ClaimsIdentity([new Claim(Saml2ClaimTypes.NameId, nameId)]),
        };

        var token = response.CreateSecurityToken(SpEntityId, subjectConfirmationLifetime: 5, issuedTokenLifetime: 60);

        if (authnInstant is { } instant)
        {
            // ⚠️ **署名する前に、認証の時刻だけ差し替える**（IdP が前のセッションを流用したときの応答を作る）
            var statements = token.Assertion.Statements;
            var original = statements.OfType<Saml2AuthenticationStatement>().Single();
            statements.Remove(original);
            statements.Add(new Saml2AuthenticationStatement(original.AuthenticationContext, instant.UtcDateTime)
            {
                SessionIndex = original.SessionIndex,
            });
        }

        var binding = new Saml2PostBinding();
        binding.Bind(response);
        return Regex.Match(binding.PostContent, "name=\"SAMLResponse\" value=\"([^\"]+)\"").Groups[1].Value;
    }

    private static Task<HttpResponseMessage> PostResponseAsync(HttpClient client, string samlResponse) =>
        client.PostAsync(
            "/api/admin/saml/acs",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("SAMLResponse", samlResponse)]));

    // ---- 管理者の用意 ---------------------------------------------------------------------

    private sealed record SamlAdmin(HttpClient Client, string LoginId);

    /// <summary>SAML で入った管理者（IdP の NameID = 本アプリのログイン ID）。</summary>
    private async Task<SamlAdmin> SamlSignedInAsync(SeededAdmin? seeded = null)
    {
        seeded ??= await host.SeedAdminAsync(withTotp: false);
        var client = host.CreateClient();

        using var login = await client.GetAsync("/api/admin/saml/login?returnUrl=/admin");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var request = ReadAuthnRequest(login.Headers.Location!.ToString());

        using var acs = await PostResponseAsync(client, SignedResponse(request.Id, seeded.LoginId));
        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.Equal("/admin", acs.Headers.Location!.OriginalString);

        using var overview = await client.GetAsync(Tokens);
        overview.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        Assert.Equal("saml-force", json.RootElement.GetProperty("reauthentication").GetString());

        return new SamlAdmin(client, seeded.LoginId);
    }

    /// <summary>再認証を始めて、偽の IdP へ向かう要求を読む。</summary>
    private static async Task<AuthnRequestFacts> StartReauthAsync(HttpClient client)
    {
        using var start = await client.PostAsJsonAsync("/api/admin/saml/reauth", new { });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var json = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        return ReadAuthnRequest(json.RootElement.GetProperty("redirectUrl").GetString()!);
    }

    private static async Task<HttpResponseMessage> IssueAsync(HttpClient client, string name = "ci") =>
        await client.PostAsJsonAsync(Tokens, new
        {
            name,
            scopes = new[] { "surveys.read" },
            expiresInDays = 30,
            ipAllowList = Array.Empty<string>(),
        });

    private static async Task<string> ReturnTargetAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        var html = await response.Content.ReadAsStringAsync();
        return Regex.Match(html, "http-equiv=\"refresh\" content=\"0;url=([^\"]+)\"").Groups[1].Value;
    }

    private static async Task<int> TokenCountAsync(HttpClient client)
    {
        using var overview = await client.GetAsync(Tokens);
        using var json = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("tokens").GetArrayLength();
    }

    // ---- 試験 ---------------------------------------------------------------------------

    [Fact]
    public async Task SAMLの管理者は強制再認証をしないとキーを発行できない()
    {
        var admin = await SamlSignedInAsync();

        // **セッションを作った直後でも、再認証なしでは作れない**（#1025 の「5 分以内」では通っていた）
        using var issue = await IssueAsync(admin.Client);

        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
        using var json = JsonDocument.Parse(await issue.Content.ReadAsStringAsync());
        Assert.Equal("reauth-required", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await TokenCountAsync(admin.Client));
    }

    [Fact]
    public async Task ForceAuthnつきの要求を出し_戻ったあとに発行でき_同じ許可は使い回せない()
    {
        var admin = await SamlSignedInAsync();

        var request = await StartReauthAsync(admin.Client);
        Assert.True(request.ForceAuthn);
        Assert.Equal(IdpSsoUrl, request.Destination);
        Assert.Equal(Acs, request.AcsUrl);

        using var back = await PostResponseAsync(admin.Client, SignedResponse(request.Id, admin.LoginId));
        Assert.Equal("/admin/me?apiTokenReauth=done", await ReturnTargetAsync(back));

        using var first = await IssueAsync(admin.Client, "first");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var issued = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.StartsWith("qat_", issued.RootElement.GetProperty("token").GetString(), StringComparison.Ordinal);

        // **1 回分だけ。** 続けて 2 本目は作れない
        using var second = await IssueAsync(admin.Client, "second");
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(1, await TokenCountAsync(admin.Client));
    }

    [Fact]
    public async Task IdPがForceAuthnを無視して古い認証の時刻を返したら発行を拒否する()
    {
        var admin = await SamlSignedInAsync();
        var request = await StartReauthAsync(admin.Client);

        // **1 時間前の認証**（IdP が自分のセッションで黙って通した）
        using var back = await PostResponseAsync(
            admin.Client, SignedResponse(request.Id, admin.LoginId, DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal("/admin/me?apiTokenReauth=failed", await ReturnTargetAsync(back));
        using var issue = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
        Assert.Equal(0, await TokenCountAsync(admin.Client));
    }

    [Fact]
    public async Task 認証の時刻を差し替えた応答でも_要求の直後の時刻なら署名は通り発行できる()
    {
        // 対照実験。上の「古い時刻」の拒否が、差し替えで署名が壊れたせいではなく、時刻の判定によることを示す
        var admin = await SamlSignedInAsync();
        var request = await StartReauthAsync(admin.Client);

        using var back = await PostResponseAsync(
            admin.Client, SignedResponse(request.Id, admin.LoginId, DateTimeOffset.UtcNow.AddSeconds(1)));

        Assert.Equal("/admin/me?apiTokenReauth=done", await ReturnTargetAsync(back));
        using var issue = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.OK, issue.StatusCode);
    }

    [Fact]
    public async Task 同じ応答の再送は許可を増やさない()
    {
        var admin = await SamlSignedInAsync();
        var request = await StartReauthAsync(admin.Client);
        var response = SignedResponse(request.Id, admin.LoginId);

        using var first = await PostResponseAsync(admin.Client, response);
        Assert.Equal("/admin/me?apiTokenReauth=done", await ReturnTargetAsync(first));
        using var issued = await IssueAsync(admin.Client, "first");
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);

        // **リプレイ**: cookie も要求も使い切っている。通常のログインとしても、出していない応答として断られる
        using var replay = await PostResponseAsync(admin.Client, response);
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Contains("samlError=", replay.Headers.Location!.OriginalString, StringComparison.Ordinal);

        using var again = await IssueAsync(admin.Client, "second");
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(1, await TokenCountAsync(admin.Client));
    }

    [Fact]
    public async Task 戻り先は固定で_呼び出し側の入力では変えられない()
    {
        var admin = await SamlSignedInAsync();

        // returnUrl を query・本文のどちらで渡しても、使わない
        using var start = await admin.Client.PostAsJsonAsync(
            "/api/admin/saml/reauth?returnUrl=https://evil.example/steal",
            new { returnUrl = "https://evil.example/steal" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var json = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var request = ReadAuthnRequest(json.RootElement.GetProperty("redirectUrl").GetString()!);

        using var back = await PostResponseAsync(admin.Client, SignedResponse(request.Id, admin.LoginId));
        var target = await ReturnTargetAsync(back);

        Assert.Equal("/admin/me?apiTokenReauth=done", target);
        Assert.DoesNotContain("evil.example", await back.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 別の人で再認証した応答は許可を作らない()
    {
        var admin = await SamlSignedInAsync();
        var other = await host.SeedAdminAsync(withTotp: false);
        var request = await StartReauthAsync(admin.Client);

        using var back = await PostResponseAsync(admin.Client, SignedResponse(request.Id, other.LoginId));

        Assert.Equal("/admin/me?apiTokenReauth=failed", await ReturnTargetAsync(back));
        using var issue = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
    }

    [Fact]
    public async Task 信頼する証明書で署名していない応答は許可を作らない()
    {
        var admin = await SamlSignedInAsync();
        var request = await StartReauthAsync(admin.Client);

        using var attacker = CreateCertificate();
        using var back = await PostResponseAsync(
            admin.Client, SignedResponse(request.Id, admin.LoginId, signer: attacker));

        Assert.Equal("/admin/me?apiTokenReauth=failed", await ReturnTargetAsync(back));
        using var issue = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
    }

    [Fact]
    public async Task 出した要求と違う応答は再認証として受けない()
    {
        var admin = await SamlSignedInAsync();
        await StartReauthAsync(admin.Client);

        using var back = await PostResponseAsync(
            admin.Client, SignedResponse("_not-the-request-we-sent", admin.LoginId));

        // 再認証の戻りではない。通常のログインとして断られ、許可は作られない
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Contains("samlError=", back.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var issue = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);
    }

    [Fact]
    public async Task 許可は別のセッションでは使えない()
    {
        var admin = await SamlSignedInAsync();
        var request = await StartReauthAsync(admin.Client);
        using var back = await PostResponseAsync(admin.Client, SignedResponse(request.Id, admin.LoginId));
        Assert.Equal("/admin/me?apiTokenReauth=done", await ReturnTargetAsync(back));

        // 同じ人の、別のブラウザ（別のセッション）
        var seeded = new SeededAdmin(admin.LoginId, string.Empty);
        var secondBrowser = await SamlSignedInAsync(seeded);
        using var issue = await IssueAsync(secondBrowser.Client);

        Assert.Equal(HttpStatusCode.Forbidden, issue.StatusCode);

        // もとのセッションは、まだ 1 回使える
        using var original = await IssueAsync(admin.Client);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
    }

    [Fact]
    public async Task 再認証の開始は別オリジンからは受けない()
    {
        var admin = await SamlSignedInAsync();

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/admin/saml/reauth")
        {
            Content = JsonContent.Create(new { }),
        };
        message.Headers.Add("Origin", "https://evil.example");
        using var response = await admin.Client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 再認証の開始はログインしていなければ受けず_SAMLで入ったセッションだけが使える()
    {
        using var anonymous = host.CreateClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync("/api/admin/saml/reauth", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // パスワードで入った管理者は、再認証の対象ではない
        var seeded = await host.SeedAdminAsync(withTotp: false);
        using var password = host.CreateClient();
        using var login = await host.LoginAsync(password, seeded);
        login.EnsureSuccessStatusCode();

        using var notSaml = await password.PostAsJsonAsync("/api/admin/saml/reauth", new { });
        Assert.Equal(HttpStatusCode.Conflict, notSaml.StatusCode);
    }
}
