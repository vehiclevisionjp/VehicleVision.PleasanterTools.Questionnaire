using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker.Tests;

public class AssetHistorySenderTests
{
    private const string ResponseToken = "token-must-not-be-sent";
    private static readonly Guid SurveyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestedBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestedBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"Id":1,"StatusCode":200}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static PendingAssetHistory Pending(int retryCount = 0) => new(
        EventId,
        SurveyId,
        1,
        ResponseToken,
        (int)AssetHistoryEventType.Download,
        AssetId,
        "資料.pdf",
        new DateTime(2026, 9, 17, 3, 0, 0, DateTimeKind.Utc),
        retryCount);

    private static SurveySnapshot Snapshot() => new(
        new SurveyDefinition
        {
            SurveyId = SurveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("履歴テスト"),
            Pages = [],
        },
        new MappingDefinition(),
        PleasanterSiteId: 100,
        ResponseJsonColumn: "DescriptionZ",
        AssetHistorySiteId: 200,
        AssetHistoryMapping: new MappingDefinition
        {
            Assignments =
            [
                ColumnAssignment.Direct("ClassA", MappingSource.System(MappingSystemValue.EventType)),
                ColumnAssignment.Direct("DateA", MappingSource.System(MappingSystemValue.OccurredAt)),
                ColumnAssignment.Direct("DescriptionA", MappingSource.System(MappingSystemValue.AssetFileName)),
                ColumnAssignment.Direct("ClassB", MappingSource.System(MappingSystemValue.AssetId)),
                ColumnAssignment.Direct("ClassC", MappingSource.System(MappingSystemValue.ReferenceId)),
                ColumnAssignment.Direct("Title", MappingSource.System(MappingSystemValue.SurveyTitle)),
            ],
        });

    private static (AssetHistorySender Sender, FakeAssetHistoryOutbox Outbox, FakeTokenStore Tokens)
        Build(RecordingHandler handler)
    {
        var outbox = new FakeAssetHistoryOutbox();
        var tokens = new FakeTokenStore();
        var client = new PleasanterApiClient(
            new HttpClient(handler),
            new PleasanterOptions
            {
                BaseUrl = "http://pleasanter",
                ApiKey = "key",
                ApiKeyUserTimeZoneId = "Asia/Tokyo",
            });
        var sender = new AssetHistorySender(
            outbox,
            tokens,
            new FakeSnapshotStore(Snapshot()),
            client,
            new PleasanterRecordBuilder(new PleasanterDateTime("Asia/Tokyo")),
            new MappingEvaluator(),
            new ResponseSenderOptions(),
            NullLogger<AssetHistorySender>.Instance);
        return (sender, outbox, tokens);
    }

    [Fact]
    public async Task ReferenceId未解決なら送信せず再試行回数を増やさず待つ()
    {
        var handler = new RecordingHandler();
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending(retryCount: 4));

        Assert.Equal(SendOutcome.Rescheduled, await sender.SendOnceAsync());

        Assert.Empty(handler.RequestedBodies);
        Assert.Equal(EventId, Assert.Single(outbox.Waited).EventId);
        Assert.Empty(outbox.Rescheduled);
        Assert.Empty(outbox.DeadLettered);
        Assert.Empty(outbox.Completed);
    }

    [Fact]
    public async Task ReferenceId解決後は全システム値だけを送る()
    {
        const long referenceId = 987654;
        var handler = new RecordingHandler();
        var (sender, outbox, tokens) = Build(handler);
        tokens.Map[ResponseToken] = referenceId;
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var bodyText = Assert.Single(handler.RequestedBodies);
        var body = JsonNode.Parse(bodyText)!.AsObject();
        Assert.Equal(
            ["ApiKey", "ApiVersion", "ClassHash", "DateHash", "DescriptionHash", "Title"],
            body.Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal("履歴テスト", body["Title"]!.GetValue<string>());
        Assert.Equal("Download", body["ClassHash"]!["ClassA"]!.GetValue<string>());
        Assert.Equal(AssetId.ToString(), body["ClassHash"]!["ClassB"]!.GetValue<string>());
        Assert.Equal(referenceId.ToString(), body["ClassHash"]!["ClassC"]!.GetValue<string>());
        Assert.Equal("資料.pdf", body["DescriptionHash"]!["DescriptionA"]!.GetValue<string>());
        Assert.Equal("2026-09-17T12:00:00", body["DateHash"]!["DateA"]!.GetValue<string>());

        Assert.DoesNotContain(ResponseToken, bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponseToken", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Answers", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ResponseJson", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IpAddress", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserAgent", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([EventId], outbox.Completed);
        Assert.Empty(outbox.Waited);
        Assert.Empty(outbox.Rescheduled);
        Assert.Empty(outbox.DeadLettered);
    }
}
