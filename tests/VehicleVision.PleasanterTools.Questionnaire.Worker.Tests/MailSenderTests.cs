using Microsoft.Extensions.Logging.Abstractions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker.Tests;

public class MailSenderTests
{
    private static readonly Guid SurveyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MailId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly FakeMailOutbox _outbox = new();
    private readonly FakeMailTransport _transport = new();
    private readonly FakeMailPayloadProtector _protector = new();
    private readonly FakeAdminNotificationStore _notifications = new();

    private MailSender Create(MailSenderOptions? options = null) => new(
        _outbox,
        _transport,
        _protector,
        options ?? new MailSenderOptions(),
        NullLogger<MailSender>.Instance,
        timeProvider: null,
        notifications: _notifications);

    private void Enqueue(int retryCount = 0)
    {
        var payload = _protector.Protect(
            new OutgoingMail("respondent@example.test", "ご回答ありがとうございました", "本文"));
        _outbox.Enqueue(new PendingMail(MailId, 1, SurveyId, payload, retryCount));
    }

    [Fact]
    public async Task 送るものが無ければ何もしない()
    {
        Assert.Equal(SendOutcome.Idle, await Create().SendOnceAsync());
    }

    [Fact]
    public async Task 送れたら行を消す()
    {
        // **宛先も本文も残さない**（送信待ちは届いていない間だけ持つ）
        Enqueue();

        Assert.Equal(SendOutcome.Sent, await Create().SendOnceAsync());
        Assert.Equal([MailId], _outbox.Completed);
        Assert.Equal("respondent@example.test", _transport.Sent.Single().ToAddress);
    }

    [Fact]
    public async Task 一時の失敗は送り直す()
    {
        Enqueue();
        _transport.Failure = () => new MailDeliveryException("繋がらない", isTransient: true);

        Assert.Equal(
            SendOutcome.Rescheduled, await Create().SendOnceAsync());
        Assert.Empty(_outbox.DeadLettered);
        Assert.Equal(MailId, _outbox.Rescheduled.Single().MailId);
    }

    [Fact]
    public async Task 恒久の失敗は送り直さずに分離する()
    {
        // **何度送っても同じ**（宛先が無い・認証が通らない）
        Enqueue();
        _transport.Failure = () => new MailDeliveryException("宛先が無い", isTransient: false);

        Assert.Equal(
            SendOutcome.DeadLettered, await Create().SendOnceAsync());
        Assert.Empty(_outbox.Rescheduled);
        Assert.Equal(MailId, _outbox.DeadLettered.Single().MailId);
    }

    [Fact]
    public async Task 再送の上限に達したら分離する()
    {
        Enqueue(retryCount: 10);
        _transport.Failure = () => new MailDeliveryException("繋がらない", isTransient: true);

        Assert.Equal(
            SendOutcome.DeadLettered,
            await Create(new MailSenderOptions { MaxRetryCount = 10 })
                .SendOnceAsync());
    }

    [Fact]
    public async Task 復号できない行は分離する()
    {
        // **鍵が変わった・行が壊れた。** 何度やっても読めない
        Enqueue();
        _protector.Broken = true;

        Assert.Equal(
            SendOutcome.DeadLettered, await Create().SendOnceAsync());
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task 分離したら管理者へ知らせる()
    {
        // ⚠️ **回答のデッドレターとは別の種類。**
        // メールが届かなかっただけで、回答は Pleasanter に入っている
        Enqueue();
        _transport.Failure = () => new MailDeliveryException("宛先が無い", isTransient: false);

        await Create().SendOnceAsync();

        Assert.Equal(
            [((int)AdminNotificationKind.MailDeadLettered, SurveyId)], _notifications.Raised);
    }

    [Fact]
    public async Task 知らせを書けなくても送信の結果を変えない()
    {
        Enqueue();
        _transport.Failure = () => new MailDeliveryException("宛先が無い", isTransient: false);
        _notifications.Throws = true;

        Assert.Equal(
            SendOutcome.DeadLettered, await Create().SendOnceAsync());
    }

    [Fact]
    public async Task 想定外の失敗は送り直す()
    {
        // **1 通の失敗で止めない。** 原因が分からないものは一時扱いにして再挑戦する
        Enqueue();
        _transport.Failure = () => new InvalidOperationException("想定外");

        Assert.Equal(
            SendOutcome.Rescheduled, await Create().SendOnceAsync());
    }

    [Fact]
    public void 再送の間隔は上限で頭打ちになる()
    {
        var options = new MailSenderOptions();
        var now = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.Add(options.RetryMaxDelay), options.NextAttemptAt(now, retryCount: 16));
    }
}
