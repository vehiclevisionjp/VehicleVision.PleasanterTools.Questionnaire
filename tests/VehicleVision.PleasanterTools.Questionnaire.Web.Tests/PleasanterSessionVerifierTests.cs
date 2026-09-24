using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>Pleasanter へ cookie を転送して本人を聞く（Issue #464）。</summary>
/// <remarks>
/// **成功と判断してよいのは、業務ステータス 200 で本人 1 行がきっちり読めたときだけ。**
/// それ以外（転送・HTML・時間切れ・壊れた JSON・5xx）を成功と取り違えないことを確かめる。
/// 応答の形は 2026-09-24 に Pleasanter 1.5.8.1 ＋ SQL Server で実測したもの。
/// </remarks>
public class PleasanterSessionVerifierTests
{
    private const string AuthenticatedJson =
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1,"LoginId":"Administrator","Name":"Administrator"}]}}}""";

    private const string BrowserCookies =
        ".AspNetCore.Cookies=chunks-2; .AspNetCore.CookiesC1=AAA; .AspNetCore.CookiesC2=BBB; "
        + "Pleasanter_SessionGuid=6f0c; q.admin=OWN; q.admin.pending=OWN2; other=zzz";

    private static PleasanterSsoOptions Options(TimeSpan? timeout = null) => new()
    {
        Enabled = true,
        InternalBaseUrl = new Uri("http://pleasanter.internal:8080/"),
        LoginUrl = "/users/login",
        Timeout = timeout ?? TimeSpan.FromSeconds(5),
    };

    private static (PleasanterSessionVerifier Verifier, FakeHttpMessageHandler Handler) Create(
        FakeHttpMessageHandler handler) =>
        (new PleasanterSessionVerifier(
            new SingleHttpClientFactory(handler),
            NullLogger<PleasanterSessionVerifier>.Instance), handler);

    [Fact]
    public async Task 業務ステータス200で本人が返れば認証済み()
    {
        var (verifier, handler) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthenticatedJson));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Equal(new PleasanterIdentity(1, 1, "Administrator", "Administrator"), result.Identity);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("http://pleasanter.internal:8080/api/extended/sql"), request.RequestUri);

        // **API キーを送らない。** 本文は版と拡張 SQL の名前だけ
        var body = Assert.Single(handler.Bodies);
        Assert.Equal("""{"ApiVersion":1.1,"Name":"QuestionnaireWhoAmI"}""", body);
        Assert.DoesNotContain("ApiKey", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 分割された認証cookieも含めて転送し本アプリのcookieは送らない()
    {
        var (verifier, handler) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthenticatedJson));

        await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        var cookie = string.Join("; ", Assert.Single(handler.Requests).Headers.GetValues("Cookie"));
        Assert.Equal(
            ".AspNetCore.Cookies=chunks-2; .AspNetCore.CookiesC1=AAA; .AspNetCore.CookiesC2=BBB; Pleasanter_SessionGuid=6f0c",
            cookie);
        Assert.DoesNotContain("q.admin", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("other", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public void cookieの値は受け取った形のまま並べる()
    {
        // **URL デコードして組み立て直さない。** 値が変わると Pleasanter が読めない
        var (header, count) = PleasanterSessionVerifier.BuildCookieHeader(
            new StringValues([".AspNetCore.Cookies=a%2Bb==; q.admin=x", "Pleasanter_SessionGuid=g"]),
            Options());

        Assert.Equal(2, count);
        Assert.Equal(".AspNetCore.Cookies=a%2Bb==; Pleasanter_SessionGuid=g", header);
    }

    [Fact]
    public async Task 転送するcookieが無ければ問い合わせずに未ログイン()
    {
        var (verifier, handler) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthenticatedJson));

        var result = await verifier.VerifyAsync(new StringValues("q.admin=OWN; other=1"), Options());

        Assert.Equal(PleasanterSessionStatus.Unauthenticated, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HTTPの401は未ログイン()
    {
        var (verifier, _) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"StatusCode":401}"""));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Unauthenticated, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task HTTP200でも業務ステータスが401なら未ログイン()
    {
        var (verifier, _) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"StatusCode":401}"""));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Unauthenticated, result.Status);
    }

    [Fact]
    public async Task 転送の応答は成功にしない()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/users/login", UriKind.Relative);
            return Task.FromResult(response);
        });
        var (verifier, _) = Create(handler);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("redirect", result.Reason);
    }

    [Fact]
    public async Task HTMLの応答は成功にしない()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html"),
        }));
        var (verifier, _) = Create(handler);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("not-json", result.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 失敗の応答は成功にも未ログインにもしない(HttpStatusCode status)
    {
        var (verifier, _) = Create(FakeHttpMessageHandler.Json(status, AuthenticatedJson));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
    }

    [Fact]
    public async Task 時間切れは成功にしない()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (verifier, _) = Create(handler);

        var result = await verifier.VerifyAsync(
            new StringValues(BrowserCookies),
            Options(TimeSpan.FromMilliseconds(50)));

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task 接続できなければ成功にしない()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException("connection refused"));
        var (verifier, _) = Create(handler);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("unreachable", result.Reason);
    }

    [Fact]
    public async Task 呼び出し元の取り消しは例外のまま伝える()
    {
        // **要求が打ち切られただけ。** 「Pleasanter に届かない」と記録しない
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (verifier, _) = Create(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(new StringValues(BrowserCookies), Options(), cancellation.Token));
    }

    [Theory]
    [InlineData("{not json", "malformed-json")]
    [InlineData("[]", "no-status")]
    [InlineData("""{"StatusCode":"200"}""", "no-status")]
    [InlineData("""{"StatusCode":500}""", "status-500")]
    [InlineData("""{"StatusCode":200}""", "no-data")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":{"Table":[]}}}""", "no-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1,"LoginId":"a"},{"TenantId":1,"UserId":2,"LoginId":"b"}]}}}""",
        "multiple-rows")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":0,"LoginId":"a"}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":"1","LoginId":"a"}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1.5,"LoginId":"a"}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1,"LoginId":" "}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":0,"UserId":1,"LoginId":"a"}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"UserId":1,"LoginId":"a"}]}}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":{"Table":[{"TenantId":1,"UserId":1,"LoginId":"a"}],"Table1":[]}}}""",
        "multiple-tables")]
    public async Task 読み切れない応答は成功にしない(string json, string reason)
    {
        var (verifier, _) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, json));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task 大きすぎる応答は読まない()
    {
        var huge = "{\"StatusCode\":200,\"Pad\":\"" + new string('x', 70 * 1024) + "\"}";
        var (verifier, _) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, huge));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
    }

    [Fact]
    public async Task 無効なら問い合わせない()
    {
        var (verifier, handler) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthenticatedJson));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), new PleasanterSsoOptions());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task 内部URLにパスがあればその下へ問い合わせる()
    {
        var (verifier, handler) = Create(FakeHttpMessageHandler.Json(HttpStatusCode.OK, AuthenticatedJson));
        var options = PleasanterSsoOptions.FromValues(key => key switch
        {
            PleasanterSsoOptions.EnabledKey => "true",
            PleasanterSsoOptions.InternalBaseUrlKey => "http://pleasanter.internal/pleasanter",
            PleasanterSsoOptions.LoginUrlKey => "/pleasanter/users/login",
            _ => null,
        });

        await verifier.VerifyAsync(new StringValues(BrowserCookies), options);

        Assert.Equal(
            new Uri("http://pleasanter.internal/pleasanter/api/extended/sql"),
            Assert.Single(handler.Requests).RequestUri);
    }
}
