using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>招待をメールで送る（Issue #189）。**DB も網も使わない。**</summary>
public class AdminInvitationMailerTests
{
    /// <summary>メールの送信待ちの代わり。**中身まで見る。**</summary>
    private sealed class FakeMailOutbox : IMailOutbox
    {
        public List<(Guid MailId, int Kind, Guid SurveyId, string Payload)> Enqueued { get; } = [];

        public Task<bool> EnqueueAsync(
            Guid mailId,
            int kind,
            Guid surveyId,
            string payloadProtected,
            CancellationToken cancellationToken = default)
        {
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

    /// <summary>暗号化の代わり。**そのまま JSON を通す**（中身を見たいため）。</summary>
    private sealed class PassThroughProtector : IMailPayloadProtector
    {
        public string Protect(OutgoingMail mail) => MailPayload.ToJson(mail);

        public OutgoingMail? Unprotect(string protectedPayload) =>
            MailPayload.FromJson(protectedPayload);
    }

    private static readonly MailOptions Ready = new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
        BaseUrl = "https://survey.example.jp",
    };

    private static readonly DateTime ExpiresAt = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static (AdminInvitationMailer Mailer, FakeMailOutbox Outbox) Create(
        MailOptions? options = null)
    {
        var outbox = new FakeMailOutbox();
        return (
            new AdminInvitationMailer(
                outbox,
                new PassThroughProtector(),
                options ?? Ready,
                NullLogger<AdminInvitationMailer>.Instance),
            outbox);
    }

    private static OutgoingMail Single(FakeMailOutbox outbox) =>
        MailPayload.FromJson(outbox.Enqueued.Single().Payload)!;

    [Fact]
    public async Task ログインIDがメールアドレスなら送る()
    {
        var (mailer, outbox) = Create();

        Assert.True(await mailer.TryEnqueueAsync("hito@example.jp", "tok-1", ExpiresAt, "ja"));

        var mail = Single(outbox);
        Assert.Equal("hito@example.jp", mail.ToAddress);
        Assert.Null(mail.FromName);
        Assert.Null(mail.ReplyToAddress);
        Assert.Null(mail.BccAddress);
        Assert.Equal((int)MailKind.AdminInvitation, outbox.Enqueued.Single().Kind);
    }

    [Fact]
    public async Task アンケートに紐づかないので空の識別子で積む()
    {
        // **NULL にしない**（NULL 同士は等しくないので突き合わせが 3 者で割れる）
        var (mailer, outbox) = Create();

        await mailer.TryEnqueueAsync("hito@example.jp", "tok-1", ExpiresAt, "ja");

        Assert.Equal(Guid.Empty, outbox.Enqueued.Single().SurveyId);
    }

    [Fact]
    public async Task 本文に受け取るURLを載せる()
    {
        var (mailer, outbox) = Create();

        await mailer.TryEnqueueAsync("hito@example.jp", "tok 1+2", ExpiresAt, "ja");

        // **記号はそのまま載せない。** URL として壊れる
        Assert.Contains(
            "https://survey.example.jp/admin/invitations/accept?token=tok%201%2B2",
            Single(outbox).Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 起点が未設定なら送らない()
    {
        // ⚠️ **要求の Host から組み立てない**（host header injection）。
        // 設定が無ければ送らず、画面の URL を手で渡してもらう
        var (mailer, outbox) = Create(Ready with { BaseUrl = null });

        Assert.False(await mailer.TryEnqueueAsync("hito@example.jp", "tok-1", ExpiresAt, "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task メールが無効なら送らない()
    {
        var (mailer, outbox) = Create(new MailOptions());

        Assert.False(await mailer.TryEnqueueAsync("hito@example.jp", "tok-1", ExpiresAt, "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task ログインIDがメールアドレスでなければ送らない()
    {
        // **ログイン ID がメールアドレスとは限らない。** 宛先が無いだけで、異常ではない
        var (mailer, outbox) = Create();

        Assert.False(await mailer.TryEnqueueAsync("yamada", "tok-1", ExpiresAt, "ja"));
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task 言語に合わせて文言を選ぶ()
    {
        var (mailer, outbox) = Create();

        await mailer.TryEnqueueAsync("hito@example.jp", "tok-1", ExpiresAt, "en");

        Assert.Equal("You have been invited to the questionnaire admin", Single(outbox).Subject);
    }

    [Fact]
    public void 識別子は招待のトークンから決まる()
    {
        // **出し直すとトークンが変わる**ので、識別子も変わって新しい 1 通になる
        Assert.Equal(
            AdminInvitationMailer.MailIdOf("tok-1"), AdminInvitationMailer.MailIdOf("tok-1"));
        Assert.NotEqual(
            AdminInvitationMailer.MailIdOf("tok-1"), AdminInvitationMailer.MailIdOf("tok-2"));
    }

    [Fact]
    public void 識別子に招待のトークンを載せない()
    {
        // ⚠️ **招待の URL は、それだけで管理者になれる値**
        Assert.DoesNotContain(
            "tok-1", AdminInvitationMailer.MailIdOf("tok-1").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自動返信と識別子がぶつからない()
    {
        // **同じ文字列を種にしても別の 1 通。** 用途ごとに接頭辞を変えてある
        await Task.CompletedTask;

        Assert.NotEqual(
            AdminInvitationMailer.MailIdOf("same"), AutoReplyDispatcher.MailIdOf("same"));
    }
}
