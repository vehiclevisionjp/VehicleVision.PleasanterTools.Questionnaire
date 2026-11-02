using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>専用資格情報との結線（Issue #829）。偽のストアと偽の HTTP ハンドラで確かめる。</summary>
public sealed class LookupSearchServiceTests
{
    private const string Secret = "LOOKUP-SECRET-KEY-ABCDEF";
    private const string WriteKey = "WRITE-KEY-MUST-NOT-BE-USED";
    private static readonly Guid SurveyId = Guid.NewGuid();

    private sealed class FakeStore(LookupSecretResolution resolution) : ILookupCredentialStore
    {
        public List<(Guid SurveyId, string CredentialId)> Calls { get; } = [];

        public Task<LookupSecretResolution> ResolveSecretAsync(
            Guid surveyId, string credentialId, CancellationToken cancellationToken = default)
        {
            Calls.Add((surveyId, credentialId));
            return Task.FromResult(resolution);
        }

        public Task<string> CreateAsync(Guid s, LookupSourceKind k, string secret, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> RotateAsync(Guid s, string id, string secret, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> SetEnabledAsync(Guid s, string id, bool e, string? by, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid s, string id, CancellationToken c = default) =>
            throw new NotSupportedException();

        public Task<LookupCredentialStatus?> FindStatusAsync(Guid s, string id, CancellationToken c = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"StatusCode":200,"Response":{"Offset":0,"PageSize":11,"TotalCount":1,"Data":[{"ClassB":"東京"}]}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static LookupDefinition Definition(string credentialId = "cred1") => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "q",
        SourceSiteId = 5,
        CredentialId = credentialId,
        Keys = [new LookupKey { QuestionId = "k1", SourceColumn = "ClassA", SourceColumnKind = LookupColumnKind.Text }],
        Targets = [new LookupTarget { QuestionId = "t1", SourceColumn = "ClassB" }],
        AllowedColumns = ["ClassB"],
    };

    private static (LookupSearchService Service, FakeStore Store, FakeHandler Handler) Create(
        LookupSecretResolution resolution)
    {
        var store = new FakeStore(resolution);
        var handler = new FakeHandler();
        var options = new StaticPleasanterOptionsProvider(new PleasanterOptions
        {
            BaseUrl = "https://pleasanter.example.test",
            ApiKey = WriteKey,
            ApiKeyUserTimeZoneId = "Asia/Tokyo",
        });
        var client = new LookupSearchClient(new HttpClient(handler));
        return (new LookupSearchService(store, options, client), store, handler);
    }

    private static readonly IReadOnlyDictionary<string, string?> Keys =
        new Dictionary<string, string?> { ["k1"] = "abc" };

    [Fact]
    public async Task 専用資格情報で検索し_回答保存用のキーは使わない()
    {
        var (service, store, handler) = Create(new(LookupSecretStatus.Available, Secret));

        var result = await service.SearchAsync(SurveyId, Definition(), Keys);

        Assert.True(result.IsSuccess);
        Assert.Equal("single", result.Outcome);
        Assert.Equal((SurveyId, "cred1"), store.Calls.Single());
        var sent = handler.Bodies.Single();
        Assert.Equal(Secret, (string?)JsonNode.Parse(sent)!["ApiKey"]);
        Assert.DoesNotContain(WriteKey, sent);
        Assert.DoesNotContain(Secret, result.ToString());
    }

    [Theory]
    [InlineData(LookupSecretStatus.Disabled, LookupSearchFailure.CredentialDisabled)]
    [InlineData(LookupSecretStatus.NotFound, LookupSearchFailure.CredentialMissing)]
    [InlineData(LookupSecretStatus.Undecryptable, LookupSearchFailure.CredentialUndecryptable)]
    public async Task 停止_不存在_復号不可なら外部へ送らない(
        LookupSecretStatus status, LookupSearchFailure failure)
    {
        var (service, _, handler) = Create(new(status));

        var result = await service.SearchAsync(SurveyId, Definition(), Keys);

        Assert.Equal(LookupSearchStatus.SourceInvalid, result.Status);
        Assert.Equal(failure, result.Failure);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task 資格情報の参照が無い定義や日本郵便は資格情報を引かない()
    {
        var (service, store, handler) = Create(new(LookupSecretStatus.Available, Secret));

        var none = await service.SearchAsync(SurveyId, Definition(credentialId: ""), Keys);
        var jp = await service.SearchAsync(
            SurveyId, Definition() with { SourceKind = LookupSourceKind.JapanPost }, Keys);

        Assert.Equal(LookupSearchFailure.InvalidDefinition, none.Failure);
        Assert.Equal(LookupSearchFailure.InvalidDefinition, jp.Failure);
        Assert.Empty(store.Calls);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task キーの不備は入力エラーになり_検索しない()
    {
        var (service, _, handler) = Create(new(LookupSecretStatus.Available, Secret));

        var result = await service.SearchAsync(
            SurveyId, Definition(), new Dictionary<string, string?> { ["k1"] = "a%b" });

        Assert.Equal(LookupSearchStatus.InvalidKeys, result.Status);
        Assert.Empty(handler.Bodies);
    }
}
