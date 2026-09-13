using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>受け付けた回答から自動返信を積む（Issue #189）。**DB も網も使わない。**</summary>
public class AutoReplyDispatcherTests
{
    private static readonly Guid SurveyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>メールの送信待ちの代わり。</summary>
    private sealed class FakeMailOutbox : IMailOutbox
    {
        public List<(Guid MailId, int Kind, Guid SurveyId, string Payload)> Enqueued { get; } = [];

        /// <summary>積もうとしたときに投げる失敗。**受付を落とさないことを見る。**</summary>
        public bool Throws { get; set; }

        public Task<bool> EnqueueAsync(
            Guid mailId,
            int kind,
            Guid surveyId,
            string payloadProtected,
            CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("積めない");
            }

            if (Enqueued.Any(entry => entry.MailId == mailId))
            {
                // **主キーの衝突と同じ振る舞い**（本物は DbException で判定する）
                return Task.FromResult(false);
            }

            Enqueued.Add((mailId, kind, surveyId, payloadProtected));
            return Task.FromResult(true);
        }

        public Task<PendingMail?> ClaimAsync(
            string lockedBy, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
            Task.FromResult<PendingMail?>(null);

        public Task CompleteAsync(Guid mailId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RescheduleAsync(
            Guid mailId,
            DateTime nextAttemptAtUtc,
            string? error,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeadLetterAsync(
            Guid mailId, string error, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailOutboxStatus(Enqueued.Count, null, 0));
    }

    /// <summary>暗号化の代わり。**印を付けて、素通ししていないことを見る。**</summary>
    private sealed class FakeProtector : IMailPayloadProtector
    {
        public string Protect(OutgoingMail mail) => "protected:" + MailPayload.ToJson(mail);

        public OutgoingMail? Unprotect(string protectedPayload) =>
            protectedPayload.StartsWith("protected:", StringComparison.Ordinal)
                ? MailPayload.FromJson(protectedPayload["protected:".Length..])
                : null;
    }

    private static readonly MailOptions Ready = new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
    };

    private static SurveyDefinition Definition(AutoReplySettings? autoReply) => new()
    {
        SurveyId = SurveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        AutoReply = autoReply,
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "mail",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("メールアドレス"),
                        Settings = new QuestionSettings { Format = TextFormat.Email },
                    },
                ],
            },
        ],
    };

    private static AutoReplySettings Enabled => new()
    {
        Enabled = true,
        ToQuestionId = "mail",
        Subject = LocalizedText.Japanese("ご回答ありがとうございました"),
        Body = LocalizedText.Japanese("受け付けました。"),
    };

    private static ResponsePayload Payload(string token = "tok-1", string address = "a@example.test") =>
        new(token, [new PayloadAnswer("mail", [address])]);

    private static (AutoReplyDispatcher Dispatcher, FakeMailOutbox Outbox) Create(
        MailOptions? options = null)
    {
        var outbox = new FakeMailOutbox();
        return (
            new AutoReplyDispatcher(
                outbox,
                new FakeProtector(),
                options ?? Ready,
                NullLogger<AutoReplyDispatcher>.Instance),
            outbox);
    }

    [Fact]
    public async Task 自動返信が無効なら積まない()
    {
        var (dispatcher, outbox) = Create();

        Assert.False(await dispatcher.TryEnqueueAsync(SurveyId, Definition(null), Payload(), "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task 有効なら積む()
    {
        var (dispatcher, outbox) = Create();

        Assert.True(
            await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja"));

        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal((int)MailKind.AutoReply, entry.Kind);
        Assert.Equal(SurveyId, entry.SurveyId);
    }

    [Fact]
    public async Task 積むのは暗号化したもの()
    {
        // ⚠️ **平文のまま DB へ渡る経路を作らない**
        var (dispatcher, outbox) = Create();

        await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja");

        Assert.StartsWith("protected:", outbox.Enqueued.Single().Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 宛先に答えていなければ積まない()
    {
        var (dispatcher, outbox) = Create();
        var payload = new ResponsePayload("tok-1", []);

        Assert.False(await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), payload, "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task サーバ側でメールが無効なら積まない()
    {
        // **積むと送れないまま溜まる。** 管理画面にも「サーバ側で無効」と出している
        var (dispatcher, outbox) = Create(new MailOptions());

        Assert.False(
            await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task 同じ回答では二重に積まない()
    {
        var (dispatcher, outbox) = Create();

        Assert.True(await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja"));
        Assert.False(await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja"));
        Assert.Single(outbox.Enqueued);
    }

    [Fact]
    public async Task 積めなくても受付の結果を変えない()
    {
        // ⚠️ **回答は既に送信待ちへ入っている。** 知らせの失敗で例外を投げない
        var (dispatcher, outbox) = Create();
        outbox.Throws = true;

        Assert.False(
            await dispatcher.TryEnqueueAsync(SurveyId, Definition(Enabled), Payload(), "ja"));
    }

    [Fact]
    public void 識別子は回答トークンから決まる()
    {
        // **同じ回答なら必ず同じ識別子**（二重に積まないために要る）
        Assert.Equal(
            AutoReplyDispatcher.MailIdOf("tok-1"), AutoReplyDispatcher.MailIdOf("tok-1"));
        Assert.NotEqual(
            AutoReplyDispatcher.MailIdOf("tok-1"), AutoReplyDispatcher.MailIdOf("tok-2"));
    }

    [Fact]
    public void 識別子に回答トークンを載せない()
    {
        // ⚠️ **回答トークンは回答を読み書きできる値。** 管理画面から見える所へ出さない
        Assert.DoesNotContain(
            "tok-1", AutoReplyDispatcher.MailIdOf("tok-1").ToString(), StringComparison.Ordinal);
    }
}
