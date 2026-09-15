using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
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
        ResponseSenderOptions? options = null,
        IAdminNotificationStore? notifications = null)
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
            NullLogger<ResponseSender>.Instance,
            notifications: notifications);

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
    public async Task デッドレターと認証失敗は管理者への知らせになる()
    {
        // **ログにしか出ていないと、見張っていない運用では誰も気付かない**（Issue #80）
        var notifications = new FakeAdminNotificationStore();
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.BadRequest, "{\"StatusCode\":400,\"Message\":\"Invalid json data.\"}")
            .Enqueue(HttpStatusCode.Unauthorized, "{}");
        var (sender, outbox, _) = Build(handler, notifications: notifications);
        outbox.Enqueue(Pending());
        outbox.Enqueue(new PendingResponse($"{Token}-2", SurveyId, 1, Payload(), 0));

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Equal(SendOutcome.Rescheduled, await sender.SendOnceAsync());

        Assert.Equal(
            [((int)AdminNotificationKind.DeadLettered, SurveyId),
             ((int)AdminNotificationKind.PleasanterUnauthorized, Guid.Empty)],
            notifications.Raised);
    }

    [Fact]
    public async Task 知らせを書けなくても送信の結果は変わらない()
    {
        // ⚠️ **知らせは気付くためのもの。** これが失敗したせいで回答の扱いを変えない
        var notifications = new FakeAdminNotificationStore { Throws = true };
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.BadRequest, "{\"StatusCode\":400,\"Message\":\"Invalid json data.\"}");
        var (sender, outbox, _) = Build(handler, notifications: notifications);
        outbox.Enqueue(Pending());

        Assert.Equal(SendOutcome.DeadLettered, await sender.SendOnceAsync());
        Assert.Single(outbox.DeadLettered);
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

    // ---- 流量の上限（Issue #72）---------------------------------------------

    [Theory]
    [InlineData(600, 100)]
    [InlineData(60, 1000)]
    [InlineData(1, 60_000)]
    public void 一分あたりの件数から送信の間隔を出す(int perMinute, int expectedMilliseconds)
    {
        // **「1 分に N 件」を許すと、分の頭に N 件を一気に投げても条件を満たす。**
        // 等間隔に均す
        var options = new ResponseSenderOptions { MaxSendsPerMinute = perMinute };

        Assert.Equal(expectedMilliseconds, (int)options.MinSendInterval.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 零以下なら流量を絞らない(int perMinute)
    {
        var options = new ResponseSenderOptions { MaxSendsPerMinute = perMinute };

        Assert.Equal(TimeSpan.Zero, options.MinSendInterval);
    }

    [Fact]
    public async Task 上限を超えて続けて送らない()
    {
        // **復旧直後に溜まった分を一斉に送ると Pleasanter をもう一度落とす**
        // （_documents/アーキテクチャ方針.md 10 章）。
        // **時計を進めない限り 1 件しか出ないこと**を、待たずに確かめる
        var handler = new StubHandler();
        var options = new ResponseSenderOptions { MaxSendsPerMinute = 60 };
        var (sender, outbox, _) = Build(handler, options: options);

        for (var i = 0; i < 5; i++)
        {
            outbox.Enqueue(new PendingResponse($"{Token}-{i}", SurveyId, 1, Payload(), 0));
        }

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        var service = new ResponseSenderHostedService(
            sender, outbox, options, NullLogger<ResponseSenderHostedService>.Instance, time);

        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);

        // **1 件目は待たずに出る**（開始時刻がそのまま次の送信予定時刻）
        await WaitForAsync(() => outbox.Completed.Count >= 1);
        Assert.Single(outbox.Completed);

        // 1 秒ごとに 1 件。**進めた分しか出ない**
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => outbox.Completed.Count >= 2);
        Assert.Equal(2, outbox.Completed.Count);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => outbox.Completed.Count >= 3);
        Assert.Equal(3, outbox.Completed.Count);

        await stopping.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>条件が満たされるまで待つ。**満たされなければ落とす。**</summary>
    /// <remarks>
    /// **偽の時計を使っていても、常駐処理が次の待ちへ入るのは実時間で起きる。**
    /// 固定の <c>Task.Delay</c> で待つと、遅い環境で揺れる
    /// （<c>_documents/テスト方針.md</c>）。
    /// </remarks>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("待っている状態にならなかった");
            }

            await Task.Delay(10);
        }

        // **「これ以上出ないこと」も確かめたい**ので、少しだけ様子を見る
        await Task.Delay(50);
    }

    private static string Payload() =>
        ResponsePayload.Create(Token, [Answer.Of("q1", "満足")], []).ToJson();
}
