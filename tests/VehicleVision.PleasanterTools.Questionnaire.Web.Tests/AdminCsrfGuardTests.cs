using Microsoft.AspNetCore.Http;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理 API の別オリジンからの状態変更の判定（Issue #907）。</summary>
public class AdminCsrfGuardTests
{
    private static HttpRequest Request(
        string method = "POST",
        string path = "/api/admin/surveys/x/suspend",
        string? site = null,
        string? origin = null,
        string scheme = "https",
        string host = "questionnaire.example.com")
    {
        var context = new DefaultHttpContext();
        var request = context.Request;
        request.Method = method;
        request.Scheme = scheme;
        request.Host = new HostString(host);
        request.Path = path;
        if (site is not null)
        {
            request.Headers["Sec-Fetch-Site"] = site;
        }

        if (origin is not null)
        {
            request.Headers.Origin = origin;
        }

        return request;
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public void Sec_Fetch_Siteが同一オリジンかnoneなら通す(string site)
    {
        Assert.True(AdminCsrfGuard.IsAllowed(Request(site: site), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData("whatever")]
    [InlineData("")]
    public void Sec_Fetch_Siteがそれ以外なら拒む(string site)
    {
        Assert.False(AdminCsrfGuard.IsAllowed(Request(site: site), out var reason));
        Assert.StartsWith("sec-fetch-site=", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Sec_Fetch_Siteが通る値でも別オリジンのOriginより優先する()
    {
        // ブラウザが付けた Sec-Fetch-Site を正とする
        Assert.True(AdminCsrfGuard.IsAllowed(
            Request(site: "same-origin", origin: "https://other.example.com"), out _));
    }

    [Fact]
    public void Sec_Fetch_Siteが複数あれば拒む()
    {
        var request = Request();
        request.Headers["Sec-Fetch-Site"] = new Microsoft.Extensions.Primitives.StringValues(
            ["same-origin", "same-origin"]);
        Assert.False(AdminCsrfGuard.IsAllowed(request, out _));
    }

    [Fact]
    public void ログには要求側が書いた未知の値をそのまま出さない()
    {
        Assert.False(AdminCsrfGuard.IsAllowed(Request(site: "evil\r\nforged"), out var reason));
        Assert.Equal("sec-fetch-site=unknown", reason);
    }

    [Theory]
    [InlineData("https://questionnaire.example.com", true)]
    [InlineData("https://QUESTIONNAIRE.example.com", true)]
    [InlineData("https://questionnaire.example.com:443", true)]
    [InlineData("http://questionnaire.example.com", false)]
    [InlineData("https://questionnaire.example.com:8443", false)]
    [InlineData("https://pleasanter.example.com", false)]
    [InlineData("https://questionnaire.example.com.evil.test", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData("https://questionnaire.example.com/questionnaire", false)]
    [InlineData("https://user@questionnaire.example.com", false)]
    public void Sec_Fetch_Siteが無ければOriginを公開オリジンと比べる(string origin, bool allowed)
    {
        Assert.Equal(allowed, AdminCsrfGuard.IsAllowed(Request(origin: origin), out var reason));
        Assert.Equal(allowed, reason is null);
    }

    [Fact]
    public void Originのポートは省略された既定のポートと比べる()
    {
        Assert.True(AdminCsrfGuard.IsAllowed(
            Request(origin: "https://localhost:8443", host: "localhost:8443"), out _));
        Assert.True(AdminCsrfGuard.IsAllowed(
            Request(origin: "http://localhost", scheme: "http", host: "localhost:80"), out _));
        Assert.False(AdminCsrfGuard.IsAllowed(
            Request(origin: "http://localhost:8080", scheme: "http", host: "localhost:8081"), out _));
    }

    [Fact]
    public void サブパスの配置でもOriginは配信元だけで比べる()
    {
        var request = Request(origin: "https://questionnaire.example.com");
        request.PathBase = "/questionnaire";
        Assert.True(AdminCsrfGuard.IsAllowed(request, out _));

        request = Request(origin: "https://pleasanter.example.com");
        request.PathBase = "/questionnaire";
        Assert.False(AdminCsrfGuard.IsAllowed(request, out _));
    }

    [Fact]
    public void 両方のヘッダーが無ければ通す()
    {
        Assert.True(AdminCsrfGuard.IsAllowed(Request(), out _));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void 読み取りは別オリジンでも検証しない(string method)
    {
        Assert.True(AdminCsrfGuard.IsAllowed(
            Request(method: method, site: "cross-site", origin: "https://evil.test"), out _));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public void 状態を変える方法はすべて検証する(string method)
    {
        Assert.False(AdminCsrfGuard.IsAllowed(Request(method: method, site: "same-site"), out _));
    }

    [Theory]
    [InlineData("/api/admin/saml/acs")]
    [InlineData("/API/Admin/SAML/ACS")]
    [InlineData("/api/admin/saml/acs/")]
    [InlineData("/api/admin/saml/slo")]
    public void 別サイトからのPOSTを意図する経路は例外(string path)
    {
        Assert.True(AdminCsrfGuard.IsAllowed(
            Request(path: path, site: "cross-site", origin: "https://idp.example.org"), out _));
    }

    [Theory]
    [InlineData("/api/admin/saml/acs/extra")]
    [InlineData("/api/admin/saml/settings")]
    [InlineData("/api/admin/saml/acs2")]
    [InlineData("/api/admin/pleasanter-sso/check")]
    [InlineData("/api/admin/login")]
    public void 例外以外は別サイトからのPOSTを拒む(string path)
    {
        Assert.False(AdminCsrfGuard.IsAllowed(Request(path: path, site: "cross-site"), out _));
    }

    [Theory]
    [InlineData("/api/forms/abc/ticket")]
    [InlineData("/api/administrator")]
    [InlineData("/api/internal/malware-scan-events")]
    public void 管理APIの外は対象にしない(string path)
    {
        Assert.True(AdminCsrfGuard.IsAllowed(Request(path: path, site: "cross-site"), out _));
    }
}
