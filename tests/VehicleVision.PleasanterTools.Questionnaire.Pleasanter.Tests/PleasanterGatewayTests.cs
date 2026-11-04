using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dnc = VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

/// <summary>
/// DotNetClient の上に組んだ <see cref="PleasanterGateway"/> の確認（Issue #844）。
/// 失敗の分類・本文の <c>StatusCode</c>・<c>BaseUrl</c> の検査・リダイレクトを辿らないこと・API キーと URL を出さないこと。
/// </summary>
public class PleasanterGatewayTests
{
    private const string ApiKey = "test-api-key-not-a-real-secret";
    private const string OkBody = """{ "StatusCode": 200, "Id": 42, "Message": "ok", "Response": { "Data": [] } }""";

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static PleasanterGateway Create(HttpMessageHandler handler, string baseUrl = "https://pleasanter.example.test") =>
        new(
            new HttpClient(handler),
            new PleasanterOptions
            {
                BaseUrl = baseUrl,
                ApiKey = ApiKey,
                ApiVersion = 1.1m,
                ApiKeyUserTimeZoneId = "Asia/Tokyo",
            });

    private static Dictionary<string, object?> Record() => new() { ["ClassA"] = "値", ["NumA"] = 3 };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task 接続試験は認証拒否だけを失敗として扱う(HttpStatusCode statusCode, bool expected)
    {
        var stub = new Stub(_ => Json(statusCode, "{}"));

        Assert.Equal(expected, await Create(stub).CheckConnectionAsync());
        Assert.Equal("https://pleasanter.example.test/api/users/Get", Assert.Single(stub.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task 接続試験は_使えないURLなら通信せずに失敗()
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, "{}"));

        Assert.False(await Create(stub, "http://pleasanter:8080").CheckConnectionAsync());
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task 作成は_APIキーとApiVersionを本文にだけ載せ_URLは_api_items_サイトID_Create()
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, OkBody));

        var response = await Create(stub, "https://pleasanter.example.test/").CreateAsync(123, Record());

        Assert.True(response.IsSuccess);
        Assert.Equal(42, response.Id);
        var (uri, body) = Assert.Single(stub.Requests);
        Assert.Equal("https://pleasanter.example.test/api/items/123/Create", uri.ToString());
        Assert.DoesNotContain(ApiKey, uri.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal(ApiKey, json["ApiKey"]!.GetValue<string>());
        Assert.Equal(1.1m, json["ApiVersion"]!.GetValue<decimal>());
        Assert.Equal("値", json["ClassA"]!.GetValue<string>());
        Assert.Equal(3, json["NumA"]!.GetValue<int>());
    }

    [Fact]
    public async Task 取得と更新とサイト操作と照合は_従来のパスと本文で呼ぶ()
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, OkBody));
        var gateway = Create(stub);

        await gateway.UpdateAsync(7, Record());
        await gateway.GetRecordAsync(7);
        await gateway.GetSiteAsync(8);
        await gateway.CreateSiteAsync(9, new Dictionary<string, object?> { ["Title"] = "t" });
        await gateway.UpdateSiteAsync(8, new Dictionary<string, object?> { ["Title"] = "t" });
        await gateway.FindByResponseTokenAsync(5, "DescriptionA", "tok");

        Assert.Equal(
            [
                "/api/items/7/Update",
                "/api/items/7/Get",
                "/api/items/8/GetSite",
                "/api/items/9/CreateSite",
                "/api/items/8/UpdateSite",
                "/api/items/5/Get",
            ],
            stub.Requests.Select(r => r.Uri.AbsolutePath));
        var find = JsonNode.Parse(stub.Requests[^1].Body)!;
        Assert.Equal("tok", find["View"]!["ColumnFilterHash"]!["DescriptionA"]!.GetValue<string>());
    }

    [Fact]
    public async Task 型付きの呼び出しをしないので_版の問い合わせ_GetLicenseInfo_は送らない()
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, OkBody));
        var gateway = Create(stub);

        await gateway.CreateAsync(1, Record());
        await gateway.CreateAsync(1, Record());

        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.DoesNotContain("GetLicenseInfo", r.Uri.ToString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.RequestTimeout, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.TemporaryRedirect, PleasanterErrorKind.Permanent)]
    public async Task HTTPの状態を失敗の種類へ分類する(HttpStatusCode status, PleasanterErrorKind expected)
    {
        var response = await Create(new Stub(_ => Json(status, "{}"))).CreateAsync(1, Record());

        Assert.Equal(expected, response.ErrorKind);
        Assert.Equal((int)status, response.StatusCode);
    }

    [Theory]
    [InlineData(401, PleasanterErrorKind.Unauthorized)]
    [InlineData(400, PleasanterErrorKind.Permanent)]
    [InlineData(403, PleasanterErrorKind.Permanent)]
    [InlineData(404, PleasanterErrorKind.Permanent)]
    [InlineData(408, PleasanterErrorKind.Transient)]
    [InlineData(429, PleasanterErrorKind.Transient)]
    [InlineData(500, PleasanterErrorKind.Transient)]
    [InlineData(409, PleasanterErrorKind.Permanent)]
    public async Task HTTPが200でも_本文のStatusCodeが200以外なら失敗にする(int bodyStatus, PleasanterErrorKind expected)
    {
        // DotNetClient #95 を含む版へ上げたら、この補いと試験は消せる
        var stub = new Stub(_ => Json(HttpStatusCode.OK, $$"""{ "StatusCode": {{bodyStatus}}, "Message": "detail" }"""));

        var response = await Create(stub).CreateAsync(1, Record());

        Assert.Equal(expected, response.ErrorKind);
        Assert.Equal(bodyStatus, response.StatusCode);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task 接続できなければ_Unknown_URLもAPIキーも出さない()
    {
        var stub = new Stub(_ => throw new HttpRequestException("connect failed https://pleasanter.example.test/ " + ApiKey));

        var response = await Create(stub).CreateAsync(1, Record());

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.Null(response.StatusCode);
        Assert.Null(response.Message);
    }

    [Fact]
    public async Task 呼び出し側の取り消しはそのまま伝わる()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gateway = Create(new Stub(_ => Json(HttpStatusCode.OK, OkBody)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.CreateAsync(1, Record(), cts.Token));
    }

    [Theory]
    [InlineData("http://pleasanter.example.com")]
    [InlineData("http://pleasanter:8080")]
    [InlineData("ftp://pleasanter.example.com")]
    [InlineData("https://user:pass@pleasanter.example.com")]
    [InlineData("https://pleasanter.example.com?x=1")]
    [InlineData("https://pleasanter.example.com#frag")]
    [InlineData("pleasanter.example.com")]
    [InlineData("")]
    public async Task 使えないURLは_通信せずにUnauthorized_固定の文言でURLもAPIキーも出さない(string baseUrl)
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, OkBody));

        var response = await Create(stub, baseUrl).CreateAsync(1, Record());

        Assert.Equal(PleasanterErrorKind.Unauthorized, response.ErrorKind);
        Assert.Null(response.StatusCode);
        Assert.Equal(PleasanterGateway.InvalidBaseUrlMessage, response.Message);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain("pass", response.Message);
        Assert.DoesNotContain("example.com", response.Message);
        Assert.DoesNotContain(ApiKey, response.Message);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("https://pleasanter.example.com/pleasanter")]
    public async Task ループバックのhttpと_パス付きのhttpsは使える(string baseUrl)
    {
        var stub = new Stub(_ => Json(HttpStatusCode.OK, OkBody));

        var response = await Create(stub, baseUrl).CreateAsync(1, Record());

        Assert.True(response.IsSuccess);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task 本番の経路は_リダイレクトを辿らず_APIキーを転送先へ再送しない()
    {
        var targetHits = 0;
        using var target = new LoopbackServer(_ => targetHits++);
        using var origin = new LoopbackServer(context =>
        {
            context.Response.StatusCode = 307;
            context.Response.RedirectLocation = $"{target.Url}/api/items/1/Create";
        });

        using var client = Dnc.PleasanterHttp.CreateClient();
        var gateway = new PleasanterGateway(
            client,
            new PleasanterOptions { BaseUrl = origin.Url, ApiKey = ApiKey, ApiKeyUserTimeZoneId = "Asia/Tokyo" });

        var response = await gateway.CreateAsync(1, Record());

        Assert.Equal(PleasanterErrorKind.Permanent, response.ErrorKind);
        Assert.Equal(307, response.StatusCode);
        Assert.Equal(0, targetHits);
    }

    /// <summary>ループバックで受ける小さなサーバー（別のホストに見立てる）。</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public LoopbackServer(Action<HttpListenerContext> handle)
        {
            // 空いているポートを探す
            for (var attempt = 0; ; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    _listener.Start();
                    Url = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                }
            }

            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    handle(context);
                    context.Response.Close();
                }
            });
        }

        public string Url { get; } = string.Empty;

        public void Dispose()
        {
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
