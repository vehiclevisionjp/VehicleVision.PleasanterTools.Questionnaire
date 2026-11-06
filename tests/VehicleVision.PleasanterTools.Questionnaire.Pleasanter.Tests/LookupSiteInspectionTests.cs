using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter.Tests;

/// <summary>参照元サイトの点検（<c>GetSite</c>。Issue #879）。偽の HTTP ハンドラで確かめる。</summary>
public sealed class LookupSiteInspectionTests
{
    private const string SecretKey = "SECRET-API-KEY-0123456789";
    private const string BaseUrl = "https://pleasanter.example.test/";

    private sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<Uri> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Uris.Add(request.RequestUri!);
            var response = respond();
            response.RequestMessage ??= request;
            return response;
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Site(
        long siteId = 123,
        long inherit = 123,
        string integrated = "[]",
        string columns = "[]") =>
        "{\"StatusCode\":200,\"Response\":{\"Data\":{\"SiteId\":" + siteId
        + ",\"InheritPermission\":" + inherit
        + ",\"Permissions\":[\"SECRET-PERMISSION,1,1\"],\"SiteSettings\":{\"IntegratedSites\":" + integrated
        + ",\"Columns\":" + columns + "}}}}";

    private static LookupSearchClient Client(FakeHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task GetSiteを読み取りだけで呼び_列と分類を返す()
    {
        var handler = new FakeHandler(() => Json(Site(columns:
            """[{"ColumnName":"NumB","LabelText":"点数","Nullable":true,"DecimalPlaces":0},{"ColumnName":"ClassC","ChoicesText":"a\nb"}]""")));

        var result = await Client(handler).InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsIntegrated);
        Assert.False(result.InheritsPermission);
        Assert.Equal("https://pleasanter.example.test/api/items/123/getsite", handler.Uris.Single().ToString());
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        Assert.Equal(SecretKey, (string?)body["ApiKey"]);
        Assert.Equal(2, body.AsObject().Count);

        var numB = result.Columns.Single(column => column.Name == "NumB");
        Assert.Equal(LookupColumnKind.Number, numB.Kind);
        Assert.True(numB.Nullable);
        Assert.Equal(0, numB.DecimalPlaces);
        Assert.Equal("点数", numB.Label);
        Assert.True(result.Columns.Single(column => column.Name == "ClassC").HasChoices);

        // 設定のない標準の列は既定の列として補う
        var classA = result.Columns.Single(column => column.Name == "ClassA");
        Assert.Equal(LookupColumnKind.Text, classA.Kind);
        Assert.False(classA.HasChoices);
        Assert.False(result.Columns.Single(column => column.Name == "NumA").Nullable);
        Assert.Null(result.Columns.Single(column => column.Name == "NumA").DecimalPlaces);
    }

    [Fact]
    public async Task 統合と継承を検出する()
    {
        var integrated = await Client(new FakeHandler(() => Json(Site(integrated: """["2","1"]"""))))
            .InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);
        var inherited = await Client(new FakeHandler(() => Json(Site(inherit: 9))))
            .InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);

        Assert.True(integrated.IsIntegrated);
        Assert.False(integrated.InheritsPermission);
        Assert.True(inherited.InheritsPermission);
    }

    [Theory]
    [InlineData(401, LookupSearchFailure.Unauthorized)]
    [InlineData(403, LookupSearchFailure.Forbidden)]
    [InlineData(404, LookupSearchFailure.NotFound)]
    [InlineData(500, LookupSearchFailure.ServerError)]
    public async Task 失敗は分類だけを返す(int status, LookupSearchFailure expected)
    {
        var handler = new FakeHandler(() => Json("""{"Message":"SECRET-BODY"}""", (HttpStatusCode)status));

        var result = await Client(handler).InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Failure);
        Assert.Empty(result.Columns);
    }

    [Fact]
    public async Task 別のサイトの応答_壊れた応答_平文HTTPは通さない()
    {
        var other = await Client(new FakeHandler(() => Json(Site(siteId: 999))))
            .InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);
        var broken = await Client(new FakeHandler(() => Json("""{"Response":{}}""")))
            .InspectSiteAsync(new LookupSourceConnection(BaseUrl, SecretKey), 123);
        var handler = new FakeHandler(() => Json(Site()));
        var insecure = await Client(handler)
            .InspectSiteAsync(new LookupSourceConnection("http://pleasanter.example.test/", SecretKey), 123);

        Assert.Equal(LookupSearchFailure.InvalidResponse, other.Failure);
        Assert.Equal(LookupSearchFailure.InvalidResponse, broken.Failure);
        Assert.Equal(LookupSearchFailure.InsecureTransport, insecure.Failure);
        Assert.Empty(handler.Uris);
    }
}
