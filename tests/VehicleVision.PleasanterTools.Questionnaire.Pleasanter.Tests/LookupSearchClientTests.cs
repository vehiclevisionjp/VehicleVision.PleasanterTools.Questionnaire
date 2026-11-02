using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

/// <summary>ルックアップ専用の検索クライアント（Issue #828）。偽の HTTP ハンドラで確かめる。</summary>
public sealed class LookupSearchClientTests
{
    private const string SecretKey = "SECRET-API-KEY-0123456789";
    private const string BaseUrl = "https://pleasanter.example.test/";

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<Uri> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            Uris.Add(request.RequestUri!);
            var response = respond(request, body);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Page(long total, int pageSize, IEnumerable<string> rows) =>
        $$$"""{"StatusCode":200,"Response":{"Offset":0,"PageSize":{{{pageSize}}},"TotalCount":{{{total}}},"Data":[{{{string.Join(",", rows)}}}]}}""";

    private static IEnumerable<string> Rows(int count) =>
        Enumerable.Range(1, count).Select(i =>
            $$$"""{"ClassB":"B{{{i}}}","NumB":0.0,"Title":"LEAK","ResultId":{{{i}}},"DescriptionA":"DO-NOT-RETURN"}""");

    private static LookupDefinition Definition(
        int maxResults = 10,
        LookupMatchMode mode = LookupMatchMode.Exact,
        bool allowPrefix = false) => new()
        {
            LookupId = "postal",
            AnchorQuestionId = "q1",
            SourceSiteId = 123,
            CredentialId = "cred",
            MaxResults = maxResults,
            Keys =
            [
                new LookupKey { QuestionId = "k1", SourceColumn = "ClassA", MatchMode = mode, AllowPrefix = allowPrefix },
                new LookupKey { QuestionId = "k2", SourceColumn = "NumA" },
            ],
            AllowedColumns = ["ClassB", "NumB"],
            DisplayColumns = ["ClassB"],
            Targets = [new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" }],
        };

    private static readonly IReadOnlyDictionary<string, LookupColumnKind> Kinds =
        new Dictionary<string, LookupColumnKind>
        {
            ["ClassA"] = LookupColumnKind.Text,
            ["NumA"] = LookupColumnKind.Number,
        };

    private static LookupSearchRequest Request(
        LookupDefinition? lookup = null,
        string? k1 = "00123",
        string? k2 = "7",
        IReadOnlyDictionary<string, LookupColumnKind>? kinds = null)
    {
        var values = new Dictionary<string, string?> { ["k1"] = k1, ["k2"] = k2 };
        return new LookupSearchRequest(lookup ?? Definition(), values, kinds ?? Kinds);
    }

    private static LookupSourceConnection Connection(string url = BaseUrl) => new(url, SecretKey);

    private static LookupSearchClient Client(FakeHandler handler, LookupSearchOptions? options = null) =>
        new(new HttpClient(handler), options);

    [Fact]
    public async Task 型別のAND条件を型付きで組み立て_K加1件を要求する()
    {
        var handler = new FakeHandler((_, _) => Json(Page(1, 11, Rows(1))));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.True(result.IsSuccess);
        Assert.Equal("https://pleasanter.example.test/api/items/123/get", handler.Uris.Single().ToString());
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal(SecretKey, (string?)body["ApiKey"]);
        Assert.Equal(0, (int?)body["Offset"]);
        Assert.Equal(11, (int?)body["PageSize"]);
        var view = body["View"]!;
        Assert.Equal("00123", (string?)view["ColumnFilterHash"]!["ClassA"]);
        Assert.Equal("7", (string?)view["ColumnFilterHash"]!["NumA"]);
        Assert.Equal(2, view["ColumnFilterHash"]!.AsObject().Count);
        Assert.Equal("ExactMatch", (string?)view["ColumnFilterSearchTypes"]!["ClassA"]);
        Assert.Equal("KeyValues", (string?)view["ApiDataType"]);
        Assert.Equal("Value", (string?)view["ApiColumnValueDisplayType"]);
        Assert.Equal(["ClassB", "NumB"], view["GridColumns"]!.AsArray().Select(n => (string?)n));
        Assert.Null(view["Search"]);
    }

    [Fact]
    public async Task 許可したキーだけ前方一致にする()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(
            Connection(),
            Request(Definition(mode: LookupMatchMode.Prefix, allowPrefix: true), k1: "ABC"));

        Assert.True(result.IsSuccess);
        var types = JsonNode.Parse(handler.Bodies.Single())!["View"]!["ColumnFilterSearchTypes"]!;
        Assert.Equal("ForwardMatch", (string?)types["ClassA"]);
        Assert.Equal("ExactMatch", (string?)types["NumA"]);
    }

    [Fact]
    public async Task 前方一致が許可されていない定義は検索しない()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(
            Connection(), Request(Definition(mode: LookupMatchMode.Prefix, allowPrefix: false), k1: "ABC"));

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(null, "7", "k1")]
    [InlineData("", "7", "k1")]
    [InlineData("   ", "7", "k1")]
    [InlineData("a%b", "7", "k1")]
    [InlineData("[\"a\"]", "7", "k1")]
    [InlineData("00123", "1.0", "k2")]
    [InlineData("00123", "abc", "k2")]
    [InlineData("00123", null, "k2")]
    public async Task キーの不備は外部検索せずに拒否する(string? k1, string? k2, string invalidId)
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(Connection(), Request(k1: k1, k2: k2));

        Assert.Equal(LookupSearchStatus.InvalidKeys, result.Status);
        Assert.Contains(invalidId, result.InvalidKeyQuestionIds);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 未知のキーIDは拒否する()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));
        var values = new Dictionary<string, string?> { ["k1"] = "a", ["k2"] = "1", ["extra"] = "x" };

        var result = await Client(handler).SearchAsync(
            Connection(), new LookupSearchRequest(Definition(), values, Kinds));

        Assert.Equal(LookupSearchStatus.InvalidKeys, result.Status);
        Assert.Contains("extra", result.InvalidKeyQuestionIds);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 列の種類が分からないキーは検索しない()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));
        var kinds = new Dictionary<string, LookupColumnKind> { ["ClassA"] = LookupColumnKind.Text };

        var result = await Client(handler).SearchAsync(Connection(), Request(kinds: kinds));

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Equal(LookupSearchFailure.InvalidDefinition, result.Failure);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(0, "notFound")]
    [InlineData(1, "single")]
    [InlineData(2, "multiple")]
    [InlineData(9, "multiple")]
    [InlineData(10, "multiple")]
    public async Task 件数をnotFound_single_multipleへ写す(int count, string outcome)
    {
        var handler = new FakeHandler((_, _) => Json(Page(count, 11, Rows(count))));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(count, result.Candidates.Length);
    }

    [Theory]
    [InlineData(10, 11, 11, 11)]
    [InlineData(10, 11, 21, 11)]
    [InlineData(20, 21, 21, 21)]
    [InlineData(20, 21, 30, 21)]
    public async Task K超過は候補を返さず絞り込みを求める(int k, int pageSize, int total, int rows)
    {
        var handler = new FakeHandler((_, _) => Json(Page(total, pageSize, Rows(rows))));

        var result = await Client(handler).SearchAsync(Connection(), Request(Definition(k)));

        Assert.Equal(LookupSearchStatus.TooManyResults, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData(19, 20)]
    [InlineData(20, 20)]
    public async Task K件ちょうどまでは返す(int count, int k)
    {
        var handler = new FakeHandler((_, _) => Json(Page(count, k + 1, Rows(count))));

        var result = await Client(handler).SearchAsync(Connection(), Request(Definition(k)));

        Assert.True(result.IsSuccess);
        Assert.Equal(count, result.Candidates.Length);
    }

    [Fact]
    public async Task 上流のページ上限がK加1未満なら一意としない()
    {
        // PageSize が 1 に縮められ、総件数 2 のうち 1 件だけが返った
        var handler = new FakeHandler((_, _) => Json(Page(2, 1, Rows(1))));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchStatus.TooManyResults, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData(1, 11, 0)]
    [InlineData(0, 11, 1)]
    [InlineData(3, 11, 2)]
    [InlineData(5, 11, 12)]
    public async Task 総件数と取得件数が合わなければ不正な応答とする(int total, int pageSize, int rows)
    {
        var handler = new FakeHandler((_, _) => Json(Page(total, pageSize, Rows(rows))));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Equal(LookupSearchFailure.Inconsistent, result.Failure);
    }

    [Fact]
    public async Task 許可列だけへ再射影し_ゼロとnullをそのまま返す()
    {
        const string row = """{"ClassB":"東京","NumB":0.0,"Title":"LEAK","ResultId":9,"DescriptionA":"DO-NOT-RETURN","Creator":1}""";
        var handler = new FakeHandler((_, _) => Json(Page(1, 11, [row])));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        var values = result.Candidates.Single().Values;
        Assert.Equal(["ClassB", "NumB"], values.Keys.Order());
        Assert.Equal("東京", values["ClassB"]);
        Assert.Equal("0.0", values["NumB"]);
        Assert.DoesNotContain("LEAK", string.Join("|", values.Values));
    }

    [Fact]
    public async Task 許可列が応答に無ければ値なしにする()
    {
        var handler = new FakeHandler((_, _) => Json(Page(1, 11, ["""{"ClassB":null}"""])));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        var values = result.Candidates.Single().Values;
        Assert.Null(values["ClassB"]);
        Assert.Null(values["NumB"]);
    }

    [Theory]
    [InlineData(401, LookupSearchStatus.SourceInvalid, LookupSearchFailure.Unauthorized)]
    [InlineData(403, LookupSearchStatus.SourceInvalid, LookupSearchFailure.Forbidden)]
    [InlineData(404, LookupSearchStatus.SourceInvalid, LookupSearchFailure.NotFound)]
    [InlineData(400, LookupSearchStatus.SourceInvalid, LookupSearchFailure.ClientError)]
    [InlineData(302, LookupSearchStatus.SourceInvalid, LookupSearchFailure.Redirected)]
    [InlineData(429, LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.RateLimited)]
    [InlineData(500, LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.ServerError)]
    [InlineData(503, LookupSearchStatus.TemporarilyUnavailable, LookupSearchFailure.ServerError)]
    public async Task HTTPの失敗を分類し_0件にはしない(
        int status, LookupSearchStatus expected, LookupSearchFailure failure)
    {
        var handler = new FakeHandler((_, _) => Json("""{"Message":"secret detail"}""", (HttpStatusCode)status));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(expected, result.Status);
        Assert.Equal(failure, result.Failure);
        Assert.Empty(result.Candidates);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task 本文側のStatusCodeが200以外なら分類し直す()
    {
        var handler = new FakeHandler((_, _) => Json("""{"StatusCode":401,"Message":"x"}"""));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchFailure.Unauthorized, result.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"StatusCode":200}""")]
    [InlineData("""{"Response":{"TotalCount":"x","Data":[]}}""")]
    [InlineData("""{"Response":{"TotalCount":1,"Data":["str"]}}""")]
    [InlineData("""{"Response":{"TotalCount":1,"Data":[{"ClassB":{"a":1}}]}}""")]
    public async Task 不正なJSONや構造は参照元の不備とする(string raw)
    {
        var handler = new FakeHandler((_, _) => Json(raw));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
    }

    [Fact]
    public async Task 応答が上限を超えたら失敗にする()
    {
        var big = new string('x', 2000);
        var handler = new FakeHandler((_, _) => Json(Page(1, 11, [$$$"""{"ClassB":"{{{big}}}"}"""])));

        var result = await Client(handler, new LookupSearchOptions { MaxResponseBytes = 1000 })
            .SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task 宣言された長さが上限を超えたら読まずに失敗にする()
    {
        var handler = new FakeHandler((_, _) =>
        {
            var response = Json(Page(0, 11, []));
            response.Content.Headers.ContentLength = 5_000_000;
            return response;
        });

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task 時間切れはタイムアウトにする()
    {
        var slow = new SlowHandler();
        var client = new LookupSearchClient(
            new HttpClient(slow), new LookupSearchOptions { Timeout = TimeSpan.FromMilliseconds(50) });

        var result = await client.SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchStatus.Timeout, result.Status);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task 呼び出し側のキャンセルは握りつぶさない()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = new LookupSearchClient(new HttpClient(new SlowHandler()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SearchAsync(Connection(), Request(), cts.Token));
    }

    [Fact]
    public async Task 接続失敗は一時障害とする()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("boom " + SecretKey));

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchStatus.TemporarilyUnavailable, result.Status);
        Assert.Equal(LookupSearchFailure.ConnectionFailed, result.Failure);
        Assert.DoesNotContain(SecretKey, result.ToString());
    }

    [Theory]
    [InlineData("http://pleasanter.example.test/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("ftp://pleasanter.example.test/")]
    public async Task HTTPSでない接続先へは送らない(string url)
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(Connection(url), Request());

        Assert.Equal(LookupSearchFailure.InsecureTransport, result.Failure);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 開発用の許可ではループバックだけHTTPを使える()
    {
        var options = new LookupSearchOptions { AllowLoopbackHttp = true };
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        Assert.True((await Client(handler, options)
            .SearchAsync(Connection("http://localhost:8080"), Request())).IsSuccess);
        var remote = await Client(handler, options)
            .SearchAsync(Connection("http://pleasanter.example.test"), Request());
        Assert.Equal(LookupSearchFailure.InsecureTransport, remote.Failure);
    }

    [Theory]
    [InlineData("https://user:pass@pleasanter.example.test/")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task 不正な接続先は送らない(string url)
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(Connection(url), Request());

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 別のホストへ追従したら失敗にする()
    {
        var handler = new FakeHandler((request, _) =>
        {
            var response = Json(Page(0, 11, []));
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://evil.example.test/api");
            return response;
        });

        var result = await Client(handler).SearchAsync(Connection(), Request());

        Assert.Equal(LookupSearchFailure.Redirected, result.Failure);
    }

    [Fact]
    public async Task 空の資格情報は送らない()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(new LookupSourceConnection(BaseUrl, ""), Request());

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task 件数の範囲外の定義は検索しない(int k)
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var result = await Client(handler).SearchAsync(Connection(), Request(Definition(k)));

        Assert.Equal(LookupSearchFailure.InvalidDefinition, result.Failure);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 日本郵便の定義やサイト未指定は検索しない()
    {
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));
        var jp = Definition() with { SourceKind = LookupSourceKind.JapanPost };
        var noSite = Definition() with { SourceSiteId = null };
        var noAllowed = Definition() with { AllowedColumns = ImmutableArray<string>.Empty };

        foreach (var lookup in new[] { jp, noSite, noAllowed })
        {
            var result = await Client(handler).SearchAsync(Connection(), Request(lookup));
            Assert.Equal(LookupSearchFailure.InvalidDefinition, result.Failure);
        }

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 接続情報と結果へAPIキーを出さない()
    {
        var handler = new FakeHandler((_, _) => Json(Page(1, 11, Rows(1))));

        var connection = Connection();
        var result = await Client(handler).SearchAsync(connection, Request());

        Assert.DoesNotContain(SecretKey, connection.ToString());
        Assert.DoesNotContain(SecretKey, result.ToString());
        Assert.DoesNotContain(SecretKey, string.Join("|", result.Candidates.SelectMany(c => c.Values.Values)));
    }

    [Fact]
    public async Task 検索値は引用符や構文を含んでも条件の意味を変えない()
    {
        // 検証で拒否されるため外部へ出ない。拒否されない値は JSON の文字列としてだけ渡る
        var handler = new FakeHandler((_, _) => Json(Page(0, 11, [])));

        var rejected = await Client(handler).SearchAsync(
            Connection(), Request(k1: "x\",\"ClassZ\":\"y"));
        Assert.Equal(LookupSearchStatus.InvalidKeys, rejected.Status);
        Assert.Empty(handler.Bodies);

        await Client(handler).SearchAsync(Connection(), Request(k1: "Own"));
        var hash = JsonNode.Parse(handler.Bodies.Single())!["View"]!["ColumnFilterHash"]!.AsObject();
        Assert.Equal(["ClassA", "NumA"], hash.Select(p => p.Key).Order());
        Assert.Equal("Own", (string?)hash["ClassA"]);
    }
}
