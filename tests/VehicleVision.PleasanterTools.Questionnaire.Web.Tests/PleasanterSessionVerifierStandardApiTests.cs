using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>標準の API で本人を聞く方式（既定、Issue #464）。</summary>
/// <remarks>
/// <para>
/// **既定は cookie 付きの <c>/api/users/get</c> を <c>UserId = Own</c> で 1 回。**
/// 403（利用者の API 利用が禁止）のときだけ、本アプリの API キーがあれば
/// <c>/api/sessions/set</c> で利用者 ID を得て、API キーで <c>/api/users/{id}/get</c> を引く。
/// </para>
/// <para>
/// 応答の形は 2026-09-25 に Pleasanter 1.5.8.1 ＋ SQL Server で実測したもの。
/// </para>
/// </remarks>
public class PleasanterSessionVerifierStandardApiTests
{
    private const string BrowserCookies =
        ".AspNetCore.Cookies=AUTH; Pleasanter_SessionGuid=6f0c; q.admin=OWN";

    private const string ForwardedCookies = ".AspNetCore.Cookies=AUTH; Pleasanter_SessionGuid=6f0c";

    private const string OwnUserJson =
        """{"StatusCode":200,"Response":{"Offset":0,"PageSize":200,"TotalCount":1,"Data":[{"TenantId":1,"UserId":2,"LoginId":"sso-user1","Name":"SSO User 1","Disabled":false}]}}""";

    private const string ForbiddenJson = """{"Id":0,"StatusCode":403,"Message":"Forbidden"}""";

    private const string UnauthorizedJson = """{"Id":0,"StatusCode":401,"Message":"Unable to authorize."}""";

    private const string SessionSetJson =
        """{"StatusCode":200,"Response":{"UserId":2,"Key":"VehicleVision.Questionnaire.SsoProbe"}}""";

    private static readonly Uri OwnUserUrl = new("http://pleasanter.internal:8080/api/users/get");
    private static readonly Uri SessionSetUrl = new("http://pleasanter.internal:8080/api/sessions/set");
    private static readonly Uri UserByIdUrl = new("http://pleasanter.api:8080/root/api/users/2/get");

    private static PleasanterSsoOptions Options(TimeSpan? timeout = null) => new()
    {
        Enabled = true,
        InternalBaseUrl = new Uri("http://pleasanter.internal:8080/"),
        LoginUrl = "/users/login",
        Timeout = timeout ?? TimeSpan.FromSeconds(5),
    };

    /// <summary>URL ごとに決まった応答を返すハンドラ。</summary>
    private static FakeHttpMessageHandler Routes(params (Uri Url, HttpStatusCode Status, string Json)[] routes) =>
        new((request, _) =>
        {
            foreach (var (url, status, json) in routes)
            {
                if (request.RequestUri == url)
                {
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
                }
            }

            throw new InvalidOperationException($"想定していない宛先: {request.RequestUri}");
        });

    private static (PleasanterSessionVerifier Verifier, ListLogger<PleasanterSessionVerifier> Logger) Create(
        FakeHttpMessageHandler handler,
        IPleasanterOptionsProvider? connection = null)
    {
        var logger = new ListLogger<PleasanterSessionVerifier>();
        return (new PleasanterSessionVerifier(
            new SingleHttpClientFactory(handler),
            connection ?? PleasanterSsoTestConnections.WithoutApiKey,
            logger), logger);
    }

    private static string? CookieOf(HttpRequestMessage request) =>
        request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;

    [Fact]
    public void 既定の方式は標準のAPI()
    {
        Assert.Equal(PleasanterSsoMethod.StandardApi, new PleasanterSsoOptions().Method);
    }

    [Fact]
    public async Task 本人1行が返れば認証済みで問い合わせは1回だけ()
    {
        var handler = Routes((OwnUserUrl, HttpStatusCode.OK, OwnUserJson));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Equal(new PleasanterIdentity(1, 2, "sso-user1", "SSO User 1"), result.Identity);

        // **API キーがあっても、禁止されていなければ使わない。** 書き込みの無い 1 回だけ
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(OwnUserUrl, request.RequestUri);
        Assert.Equal(ForwardedCookies, CookieOf(request));

        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain("ApiKey", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(1.1m, document.RootElement.GetProperty("ApiVersion").GetDecimal());
        Assert.Equal(
            """["Own"]""",
            document.RootElement.GetProperty("View").GetProperty("ColumnFilterHash").GetProperty("UserId").GetString());
    }

    [Fact]
    public async Task cookieを付けない問い合わせの401は未ログイン()
    {
        var (verifier, _) = Create(Routes((OwnUserUrl, HttpStatusCode.Unauthorized, UnauthorizedJson)));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Unauthenticated, result.Status);
        Assert.Equal("http-401", result.Reason);
    }

    [Theory]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":0,"Data":[]}}""",
        "no-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":2,"Data":[{"TenantId":1,"UserId":1,"LoginId":"Administrator"},{"TenantId":1,"UserId":2,"LoginId":"sso-user1"}]}}""",
        "multiple-rows")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":2,"Data":[{"TenantId":1,"UserId":2,"LoginId":"sso-user1"}]}}""",
        "multiple-rows")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[]}}""",
        "no-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[{"TenantId":1,"UserId":1,"LoginId":"a"},{"TenantId":1,"UserId":2,"LoginId":"b"}]}}""",
        "multiple-rows")]
    [InlineData(
        """{"StatusCode":200,"Response":{"Data":[{"TenantId":1,"UserId":2,"LoginId":"sso-user1"}]}}""",
        "no-total-count")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":{"Table":[]}}}""",
        "no-data")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[{"TenantId":0,"UserId":2,"LoginId":"sso-user1"}]}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[{"TenantId":1,"UserId":0,"LoginId":"sso-user1"}]}}""",
        "invalid-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[{"TenantId":1,"UserId":2,"LoginId":""}]}}""",
        "invalid-row")]
    [InlineData("""{"StatusCode":500}""", "status-500")]
    [InlineData("{not json", "malformed-json")]
    public async Task 本人1行と読めない応答は成功にしない(string json, string reason)
    {
        var (verifier, _) = Create(Routes((OwnUserUrl, HttpStatusCode.OK, json)));

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Identity);
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
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task 失敗の応答は代わりの経路へ回さない(HttpStatusCode status)
    {
        var handler = Routes((OwnUserUrl, status, ForbiddenJson));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Single(handler.Requests);
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
    public async Task 禁止の403でAPIキーが無ければ上流エラーにして設定を案内する()
    {
        var handler = Routes((OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson));
        var (verifier, logger) = Create(handler, PleasanterSsoTestConnections.WithoutApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("http-403", result.Reason);
        Assert.Single(handler.Requests);
        Assert.Contains(logger.Messages, message =>
            message.Contains("DisableApi", StringComparison.Ordinal)
            && message.Contains(AppSettingsProvider.PleasanterApiKeyKey, StringComparison.Ordinal)
            && message.Contains("ExtendedSql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 禁止の403でAPIキーがあれば利用者IDを得てAPIキーで引き直す()
    {
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, SessionSetJson),
            (UserByIdUrl, HttpStatusCode.OK, OwnUserJson));
        var (verifier, logger) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
        Assert.Equal(new PleasanterIdentity(1, 2, "sso-user1", "SSO User 1"), result.Identity);
        Assert.Equal([OwnUserUrl, SessionSetUrl, UserByIdUrl], handler.Requests.Select(request => request.RequestUri));

        // **sessions/set は cookie で。API キーは載せない。** セッションごとの小さな値だけを書く
        var sessionRequest = handler.Requests[1];
        Assert.Equal(ForwardedCookies, CookieOf(sessionRequest));
        using (var sessionBody = JsonDocument.Parse(handler.Bodies[1]))
        {
            Assert.Equal(
                PleasanterSessionVerifier.SessionProbeKey,
                sessionBody.RootElement.GetProperty("SessionKey").GetString());
            Assert.False(string.IsNullOrEmpty(sessionBody.RootElement.GetProperty("SessionValue").GetString()));
            Assert.False(sessionBody.RootElement.TryGetProperty("SavePerUser", out _));
            Assert.False(sessionBody.RootElement.TryGetProperty("ApiKey", out _));
        }

        // **API キーの問い合わせには cookie を付けない。** 宛先は本アプリの接続設定の URL
        var userRequest = handler.Requests[2];
        Assert.Null(CookieOf(userRequest));
        using (var userBody = JsonDocument.Parse(handler.Bodies[2]))
        {
            Assert.Equal(PleasanterSsoTestConnections.ApiKey, userBody.RootElement.GetProperty("ApiKey").GetString());
        }

        // **cookie を付けた要求には API キーが無い**
        Assert.All(
            handler.Requests.Zip(handler.Bodies).Where(pair => CookieOf(pair.First) is not null),
            pair => Assert.DoesNotContain(PleasanterSsoTestConnections.ApiKey, pair.Second, StringComparison.Ordinal));

        // **API キーも cookie の値もログに出さない**
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(PleasanterSsoTestConnections.ApiKey, message, StringComparison.Ordinal);
            Assert.DoesNotContain("AUTH", message, StringComparison.Ordinal);
            Assert.DoesNotContain("6f0c", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task 業務ステータスの403でも代わりの経路へ回る()
    {
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.OK, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, SessionSetJson),
            (UserByIdUrl, HttpStatusCode.OK, OwnUserJson));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Authenticated, result.Status);
    }

    [Fact]
    public async Task 代わりの経路でsessionsの401は未ログイン()
    {
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.Unauthorized, UnauthorizedJson));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.Unauthenticated, result.Status);
        Assert.Equal("session-http-401", result.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{"StatusCode":200,"Response":{"Key":"x"}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"UserId":0}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"UserId":"2"}}""")]
    public async Task 代わりの経路で利用者IDが読めなければ上流エラー(string json)
    {
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, json));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("session-no-user-id", result.Reason);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "user-http-401")]
    [InlineData(HttpStatusCode.Forbidden, "user-http-403")]
    public async Task APIキーの問い合わせの401と403は未ログインではなく上流エラー(HttpStatusCode status, string reason)
    {
        // **ここでの 401・403 はキーの誤りやキーの持ち主の禁止。** 利用者がログアウトしたのではない
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, SessionSetJson),
            (UserByIdUrl, status, status == HttpStatusCode.Unauthorized ? UnauthorizedJson : ForbiddenJson));
        var (verifier, logger) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.All(logger.Messages, message =>
            Assert.DoesNotContain(PleasanterSsoTestConnections.ApiKey, message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":1,"Data":[{"TenantId":1,"UserId":3,"LoginId":"someone"}]}}""",
        "user-mismatch")]
    [InlineData("""{"StatusCode":200,"Response":{"TotalCount":0,"Data":[]}}""", "user-no-row")]
    [InlineData(
        """{"StatusCode":200,"Response":{"TotalCount":2,"Data":[{"TenantId":1,"UserId":2,"LoginId":"a"},{"TenantId":1,"UserId":2,"LoginId":"b"}]}}""",
        "user-multiple-rows")]
    public async Task APIキーで引いた行が利用者IDの本人1行でなければ上流エラー(string json, string reason)
    {
        var handler = Routes(
            (OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson),
            (SessionSetUrl, HttpStatusCode.OK, SessionSetJson),
            (UserByIdUrl, HttpStatusCode.OK, json));
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task 時間切れは代わりの経路を含めた全体に掛かる()
    {
        // **1 回ごとの時間ではない。** 3 回それぞれが時間内でも、合計が超えれば時間切れ
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
            var json = request.RequestUri == OwnUserUrl ? ForbiddenJson
                : request.RequestUri == SessionSetUrl ? SessionSetJson
                : OwnUserJson;
            var status = request.RequestUri == OwnUserUrl ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var (verifier, _) = Create(handler, PleasanterSsoTestConnections.WithApiKey);

        var result = await verifier.VerifyAsync(
            new StringValues(BrowserCookies),
            Options(TimeSpan.FromMilliseconds(300)));

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task 接続設定を読めなければAPIキーが無いものとして扱う()
    {
        var handler = Routes((OwnUserUrl, HttpStatusCode.Forbidden, ForbiddenJson));
        var (verifier, _) = Create(handler, new ThrowingPleasanterOptionsProvider());

        var result = await verifier.VerifyAsync(new StringValues(BrowserCookies), Options());

        Assert.Equal(PleasanterSessionStatus.UpstreamError, result.Status);
        Assert.Equal("http-403", result.Reason);
        Assert.Single(handler.Requests);
    }

    private sealed class ThrowingPleasanterOptionsProvider : IPleasanterOptionsProvider
    {
        public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default) =>
            throw new FormatException("broken");
    }
}
