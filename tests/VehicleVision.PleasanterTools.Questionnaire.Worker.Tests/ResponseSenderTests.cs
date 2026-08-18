using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker.Tests;

public class ResponseSenderTests
{
    private const string Token = "tok-0123456789abcdef";
    private static readonly Guid SurveyId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>決められた応答を順に返す。<c>null</c> を積むと「到達できない」を模す。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode? Status, string Body)> _responses = new();

        public List<string> RequestedPaths { get; } = [];

        /// <summary>送った本文。**何を Pleasanter へ渡したかを見るため。**</summary>
        public List<string> RequestedBodies { get; } = [];

        public StubHandler Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue((status, body));
            return this;
        }

        /// <summary>到達できない場合。</summary>
        public StubHandler EnqueueUnreachable()
        {
            _responses.Enqueue((null, string.Empty));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.Content is not null)
            {
                RequestedBodies.Add(
                    await request.Content.ReadAsStringAsync(cancellationToken));
            }

            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "{\"Id\":1,\"StatusCode\":200}");

            if (status is null)
            {
                throw new HttpRequestException("到達できない");
            }

            return new HttpResponseMessage(status.Value)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static SurveySnapshot Snapshot(string? responseJsonColumn = "DescriptionA") => new(
        new SurveyDefinition
        {
            SurveyId = SurveyId.ToString(),
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages =
            [
                new Page
                {
                    PageId = "p1",
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q1",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("q1"),
                        },
                    ],
                },
            ],
        },
        new MappingDefinition
        {
            Assignments = [ColumnAssignment.Direct("ClassA", new MappingSource("q1"))],
        },
        PleasanterSiteId: 100,
        ResponseJsonColumn: responseJsonColumn);

    private static PendingResponse Pending(int retryCount = 0) => new(
        Token,
        SurveyId,
        1,
        ResponsePayload.Create(Token, [Answer.Of("q1", "満足")]).ToJson(),
        retryCount);

    private static (ResponseSender Sender, FakeOutbox Outbox, FakeTokenStore Tokens) Build(
        StubHandler handler,
        SurveySnapshot? snapshot = null,
        MappingDefinition? mapping = null,
        ResponseSenderOptions? options = null)
    {
        var outbox = new FakeOutbox();
        var tokens = new FakeTokenStore();
        var effective = snapshot ?? Snapshot();
        if (mapping is not null)
        {
            effective = effective with { Mapping = mapping };
        }

        var client = new PleasanterApiClient(
            new HttpClient(handler),
            new PleasanterOptions
            {
                BaseUrl = "http://pleasanter",
                ApiKey = "key",
                ApiKeyUserTimeZoneId = "Asia/Tokyo",
            });

        var sender = new ResponseSender(
            outbox,
            tokens,
            new FakeSnapshotStore(effective),
            client,
            new PleasanterRecordBuilder(new PleasanterDateTime("Asia/Tokyo")),
            new MappingEvaluator(),
            options ?? new ResponseSenderOptions(),
            NullLogger<ResponseSender>.Instance);

        return (sender, outbox, tokens);
    }

    [Fact]
    public async Task 送るものが無ければ何もしない()
    {
        var (sender, _, _) = Build(new StubHandler());

        Assert.Equal(SendOutcome.Idle, await sender.SendOnceAsync());
    }

    [Fact]
    public async Task 正本の列へ添付のBase64を載せない()
    {
        // **列が添付本文で埋まるうえ、応答不明時の照合はこの列を部分一致で引く**
        var content = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var base64 = Convert.ToBase64String(content);
        var payload = ResponsePayload.Create(
            Token,
            [Answer.Of("q1", "満足"), new Answer("q2", []) { FileNames = ["a.png"] }],
            [new AnsweredAttachment("q2", new IncomingAttachment("a.png", content))]);

        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"Id\":1,\"StatusCode\":200}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(new PendingResponse(Token, SurveyId, 1, payload.ToJson(), 0));

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());

        var body = Assert.Single(handler.RequestedBodies);
        Assert.DoesNotContain(base64, body, StringComparison.Ordinal);
        // ファイル名は正本に残す
        Assert.Contains("a.png", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 成功したら消してReferenceIdを覚える()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"Id\":4321,\"StatusCode\":200}");
        var (sender, outbox, tokens) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());
        Assert.Equal([Token], outbox.Completed);
        // **次回の編集で Update に回すため**
        Assert.Equal(4321, tokens.Map[Token]);
        Assert.Contains("/api/items/100/Create", handler.RequestedPaths);
    }

    [Fact]
    public async Task 既にReferenceIdがあればUpdateを呼ぶ()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"Id\":999,\"StatusCode\":200}");
        var (sender, outbox, tokens) = Build(handler);
        tokens.Map[Token] = 999;
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());
        Assert.Contains("/api/items/999/Update", handler.RequestedPaths);
    }

    [Fact]
    public async Task 恒久的な失敗はデッドレターへ回す()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.BadRequest, "{\"StatusCode\":400,\"Message\":\"Invalid json data.\"}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Single(outbox.DeadLettered);
        Assert.Empty(outbox.Rescheduled);
    }

    [Fact]
    public async Task 一時的な失敗は再送する()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, "{}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Rescheduled, await sender.SendOnceAsync());
        Assert.Single(outbox.Rescheduled);
        Assert.Empty(outbox.DeadLettered);
    }

    [Fact]
    public async Task 認証失敗は再送する()
    {
        // **キーの失効・設定ミスは人が直すが、直れば同じ回答が送れる**
        var handler = new StubHandler().Enqueue(HttpStatusCode.Unauthorized, "{}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Rescheduled, await sender.SendOnceAsync());
    }

    [Fact]
    public async Task 応答不明で照合してレコードが無ければ再送する()
    {
        var handler = new StubHandler()
            .EnqueueUnreachable()
            .Enqueue(HttpStatusCode.OK, "{\"StatusCode\":200,\"Response\":{\"Data\":[]}}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Rescheduled, await sender.SendOnceAsync());
        Assert.Empty(outbox.DeadLettered);
    }

    [Fact]
    public async Task 応答不明で照合してレコードがあれば完了させる()
    {
        // **二重登録を作らない**
        var handler = new StubHandler()
            .EnqueueUnreachable()
            .Enqueue(
                HttpStatusCode.OK,
                "{\"StatusCode\":200,\"Response\":{\"Data\":[{\"ResultId\":777}]}}");
        var (sender, outbox, tokens) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.Sent, await sender.SendOnceAsync());
        Assert.Equal([Token], outbox.Completed);
        Assert.Equal(777, tokens.Map[Token]);
    }

    [Fact]
    public async Task 応答不明で照合して複数見つかればデッドレターへ回す()
    {
        var handler = new StubHandler()
            .EnqueueUnreachable()
            .Enqueue(
                HttpStatusCode.OK,
                "{\"StatusCode\":200,\"Response\":{\"Data\":[{\"ResultId\":1},{\"ResultId\":2}]}}");
        var (sender, outbox, _) = Build(handler);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Contains("二重登録", outbox.DeadLettered.Single().Error);
    }

    [Fact]
    public async Task 回答JSON列が無ければ応答不明を自動再送しない()
    {
        // **照合できないので、二重登録を作らない側に倒す**
        var handler = new StubHandler().EnqueueUnreachable();
        var (sender, outbox, _) = Build(handler, Snapshot(responseJsonColumn: null));
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Empty(outbox.Rescheduled);
    }

    [Fact]
    public async Task マッピングの不備はデッドレターへ回す()
    {
        // **回答そのものは捨てない。人が対処できるようにする**
        var brokenMapping = new MappingDefinition
        {
            Assignments =
            [
                new ColumnAssignment
                {
                    TargetColumn = "ClassA",
                    Sources = [new MappingSource("q1"), new MappingSource("q1")],
                    Converter = null,
                },
            ],
        };
        var (sender, outbox, _) = Build(new StubHandler(), mapping: brokenMapping);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Contains("マッピングの不備", outbox.DeadLettered.Single().Error);
    }

    [Fact]
    public async Task 再送の上限を超えたらデッドレターへ回す()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.InternalServerError, "{}");
        var options = new ResponseSenderOptions { MaxRetryCount = 3 };
        var (sender, outbox, _) = Build(handler, options: options);
        outbox.Enqueue(Pending(retryCount: 3));

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Contains("再送の上限", outbox.DeadLettered.Single().Error);
    }

    [Fact]
    public async Task 読めない回答はデッドレターへ回す()
    {
        var (sender, outbox, _) = Build(new StubHandler());
        outbox.Enqueue(new PendingResponse(Token, SurveyId, 1, "これは JSON ではない", 0));

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
    }

    [Fact]
    public void 再送間隔は指数的に伸びて上限で止まる()
    {
        var options = new ResponseSenderOptions
        {
            RetryBaseDelay = TimeSpan.FromSeconds(10),
            RetryMaxDelay = TimeSpan.FromMinutes(30),
        };
        var now = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddSeconds(10), options.NextAttemptAt(now, 0));
        Assert.Equal(now.AddSeconds(20), options.NextAttemptAt(now, 1));
        Assert.Equal(now.AddSeconds(40), options.NextAttemptAt(now, 2));

        // **一斉送信で Pleasanter を再び落とさないための上限**
        Assert.Equal(now.AddMinutes(30), options.NextAttemptAt(now, 99));
    }
}
