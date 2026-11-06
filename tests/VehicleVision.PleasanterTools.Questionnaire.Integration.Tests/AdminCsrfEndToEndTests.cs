using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VehicleVision.PleasanterTools.Core.Web.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理 API が、別オリジンからの状態変更を拒むことを、実アプリの host（SQLite）で確かめる（Issue #907）。
/// </summary>
/// <remarks>
/// 試験の <c>HttpClient</c> は <c>Origin</c> も <c>Sec-Fetch-Site</c> も付けない（既存の管理 API の試験が
/// そのまま通る根拠。ブラウザではなく curl などと同じ）。ここでは要求ごとに付けて確かめる。
/// </remarks>
public sealed class AdminCsrfEndToEndTests
{
    private const string Suspend = "/api/admin/surveys/00000000-0000-0000-0000-000000000001/suspend";

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string path,
        string? site = null,
        string? origin = null,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { }),
        };
        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    /// <summary>本アプリが CSRF の検証で拒んだ応答か（認証・認可・存在の 403・404 と区別する）。</summary>
    private static async Task<bool> IsRejectedAsync(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        var text = await response.Content.ReadAsStringAsync();
        return text.Contains(AdminCsrfGuard.RejectedCode, StringComparison.Ordinal);
    }

    private static async Task<HttpClient> SignedInAsync(AdminContractHost host)
    {
        var admin = await host.SeedAdminAsync(withTotp: false);
        var client = host.CreateClient();
        using var login = await host.LoginAsync(client, admin);
        login.EnsureSuccessStatusCode();
        return client;
    }

    [Theory]
    [InlineData("same-origin", null, false)]
    [InlineData("none", null, false)]
    [InlineData("same-site", null, true)]
    [InlineData("cross-site", null, true)]
    [InlineData("same-origin", "https://evil.example.com", false)]
    [InlineData(null, "https://localhost", false)]
    [InlineData(null, "https://localhost:443", false)]
    [InlineData(null, "https://pleasanter.localhost", true)]
    [InlineData(null, "http://localhost", true)]
    [InlineData(null, "null", true)]
    [InlineData(null, null, false)]
    public async Task 状態を変える要求はヘッダーで通すか拒む(string? site, string? origin, bool rejected)
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = await SignedInAsync(host);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var response = await PostAsync(client, Suspend, site, origin, method);
            Assert.Equal(rejected, await IsRejectedAsync(response));
            if (!rejected)
            {
                // 検証は通った。あとは認証・存在・メソッドの結果（401 には落ちない）
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task 拒否の応答は短いJSONで内部情報を含まない()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = await SignedInAsync(host);

        using var response = await PostAsync(client, Suspend, site: "same-site");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("code", property.Name);
        Assert.Equal(AdminCsrfGuard.RejectedCode, property.Value.GetString());
    }

    [Fact]
    public async Task 拒否した要求は実行されない()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = await SignedInAsync(host);

        // 自分のセッションを失効させる要求。通ってしまえば、この後の読み取りが 401 になる
        using var rejected = await PostAsync(client, "/api/admin/logout", site: "same-site");
        Assert.True(await IsRejectedAsync(rejected));
        using var stillSignedIn = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

        using var allowed = await PostAsync(client, "/api/admin/logout", site: "same-origin");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var signedOut = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task 未認証でもログインの試行は別オリジンから受け付けない()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = host.CreateClient();

        foreach (var path in new[] { "/api/admin/login", "/api/admin/setup", "/api/admin/pleasanter-sso/check" })
        {
            using var crossSite = await PostAsync(client, path, site: "cross-site");
            Assert.True(await IsRejectedAsync(crossSite), path);
        }
    }

    [Theory]
    [InlineData("GET", "/api/admin/users")]
    [InlineData("GET", "/api/admin/session")]
    public async Task 読み取りは別オリジンでも検証しない(string method, string path)
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = await SignedInAsync(host);

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Origin", "https://evil.example.com");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/saml/acs")]
    [InlineData("/api/admin/saml/slo")]
    public async Task IdPからのSAMLの別サイトのPOSTは検証の例外(string path)
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await PostAsync(
            client, path, site: "cross-site", origin: "https://idp.example.org");

        // SAML は無効の構成。検証を通って、口の本来の応答（404）まで届く
        Assert.False(await IsRejectedAsync(response));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 例外の経路のほかのSAMLの口は別サイトのPOSTを拒む()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = host.CreateClient();

        foreach (var path in new[] { "/api/admin/saml/settings/test", "/api/admin/saml/acs/extra" })
        {
            using var response = await PostAsync(client, path, site: "cross-site");
            Assert.True(await IsRejectedAsync(response), path);
        }

        using var put = await PostAsync(
            client, "/api/admin/saml/settings", site: "same-site", method: HttpMethod.Put);
        Assert.True(await IsRejectedAsync(put));
    }

    [Fact]
    public async Task 回答画面のAPIは検証の対象外()
    {
        await using var host = await AdminContractHost.StartAsync();
        using var client = host.CreateClient();

        using var ticket = await PostAsync(
            client, "/api/forms/not-published/ticket", site: "cross-site", origin: "https://evil.example.com");
        Assert.False(await IsRejectedAsync(ticket));

        using var form = await client.GetAsync("/api/forms/not-published");
        Assert.False(await IsRejectedAsync(form));
    }

    [Fact]
    public async Task サブパスの配置でも例外と照合が効く()
    {
        await using var host = await AdminContractHost.StartAsync(
            settings: new Dictionary<string, string> { [PathBaseOptions.Setting] = "/questionnaire" });
        var admin = await host.SeedAdminAsync(withTotp: false);
        using var client = host.CreateClient();

        using var login = await PostLoginAsync(client, admin, "/questionnaire");
        login.EnsureSuccessStatusCode();

        var suspend = "/questionnaire" + Suspend;
        using var sameOrigin = await PostAsync(client, suspend, origin: "https://localhost");
        Assert.False(await IsRejectedAsync(sameOrigin));
        Assert.Equal(HttpStatusCode.NotFound, sameOrigin.StatusCode);

        using var otherSite = await PostAsync(client, suspend, origin: "https://pleasanter.localhost");
        Assert.True(await IsRejectedAsync(otherSite));

        // PathBase を剥がした後の Path で例外を照合する
        using var acs = await PostAsync(
            client, "/questionnaire/api/admin/saml/acs", site: "cross-site", origin: "https://idp.example.org");
        Assert.False(await IsRejectedAsync(acs));
        Assert.Equal(HttpStatusCode.NotFound, acs.StatusCode);

        // サブパスの外はそもそも本アプリの経路ではない
        using var outside = await PostAsync(client, Suspend, site: "cross-site");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, SeededAdmin admin, string pathBase) =>
        await client.PostAsJsonAsync(
            pathBase + "/api/admin/login",
            new { loginId = admin.LoginId, password = admin.Password });
}
