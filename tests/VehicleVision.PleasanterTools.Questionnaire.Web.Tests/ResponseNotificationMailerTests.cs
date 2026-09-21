using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答通知メールの集約を DB と通信なしで確かめる（Issue #357）。</summary>
public class ResponseNotificationMailerTests
{
    private static readonly Guid SurveyId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Now =
        new(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc);

    private sealed class FakeNotificationStore : IResponseNotificationStore
    {
        public List<ResponseNotificationDigest> Due { get; } = [];
        public List<(ResponseNotificationDigest Digest, IReadOnlyList<ProtectedResponseNotificationMail> Mails)>
            Queued
        { get; } = [];

        public Task<IReadOnlyList<ResponseNotificationDigest>> ListDueResponseDigestsAsync(
            DateTime dueBefore,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ResponseNotificationDigest>>(
                Due.Where(row =>
                        (row.LastMailQueuedAt ?? row.FirstOccurredAt) <= dueBefore
                        && row.Count > row.MailQueuedCount)
                    .ToList());

        public Task<bool> TryQueueResponseDigestAsync(
            ResponseNotificationDigest digest,
            IReadOnlyList<ProtectedResponseNotificationMail> mails,
            DateTime queuedAt,
            CancellationToken cancellationToken = default)
        {
            Queued.Add((digest, mails));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeProtector : IMailPayloadProtector
    {
        public string Protect(OutgoingMail mail) => MailPayload.ToJson(mail);
        public OutgoingMail? Unprotect(string protectedPayload) => MailPayload.FromJson(protectedPayload);
    }

    private static ResponseNotificationDigest Digest(
        int count = 3,
        int queuedCount = 0,
        DateTime? lastQueuedAt = null) =>
        new(
            SurveyId,
            "満足度アンケート",
            count,
            queuedCount,
            Now.AddDays(-2),
            Now.AddHours(-2),
            lastQueuedAt);

    [Fact]
    public async Task 一日経つまでメールを積まない()
    {
        var notifications = new FakeNotificationStore();
        notifications.Due.Add(Digest() with { FirstOccurredAt = Now.AddHours(-23) });
        var users = await UsersAsync("admin@example.test", "ja");
        var mailer = Create(notifications, users);

        Assert.Equal(0, await mailer.QueueDueAsync());
        Assert.Empty(notifications.Queued);
    }

    [Fact]
    public async Task 希望した管理者へ未送信件数だけをまとめる()
    {
        var notifications = new FakeNotificationStore();
        notifications.Due.Add(Digest(count: 13, queuedCount: 10, lastQueuedAt: Now.AddDays(-1)));
        var users = await UsersAsync("admin@example.test", "ja");
        var mailer = Create(notifications, users);

        Assert.Equal(1, await mailer.QueueDueAsync());

        var queued = Assert.Single(notifications.Queued);
        var protectedMail = Assert.Single(queued.Mails);
        var mail = MailPayload.FromJson(protectedMail.PayloadProtected);
        Assert.Equal("admin@example.test", mail!.ToAddress);
        Assert.Contains("3 件", mail.Body, StringComparison.Ordinal);
        Assert.Contains("満足度アンケート", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("回答内容:", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 英語設定の管理者には英語で積む()
    {
        var notifications = new FakeNotificationStore();
        notifications.Due.Add(Digest());
        var users = await UsersAsync("admin@example.test", "en");

        await Create(notifications, users).QueueDueAsync();

        var mail = MailPayload.FromJson(
            Assert.Single(Assert.Single(notifications.Queued).Mails).PayloadProtected);
        Assert.Equal("New survey responses", mail!.Subject);
        Assert.Contains("3 new response(s)", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 受取人がいなくても過去分を確定する()
    {
        var notifications = new FakeNotificationStore();
        notifications.Due.Add(Digest());

        Assert.Equal(
            1,
            await Create(
                notifications,
                new FakeAdminUserStore(new FakeTimeProvider(new DateTimeOffset(Now))))
                .QueueDueAsync());
        Assert.Empty(Assert.Single(notifications.Queued).Mails);
    }

    private static ResponseNotificationMailer Create(
        FakeNotificationStore notifications,
        IAdminUserStore users) =>
        new(
            notifications,
            users,
            new FakeProtector(),
            NullLogger<ResponseNotificationMailer>.Instance,
            new FakeTimeProvider(new DateTimeOffset(Now)));

    private static async Task<IAdminUserStore> UsersAsync(string loginId, string language)
    {
        var store = new FakeAdminUserStore(new FakeTimeProvider(new DateTimeOffset(Now)));
        await store.CreateAsync(new AdminUser
        {
            AdminUserId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            LoginId = loginId,
            PasswordHash = "hash",
            Role = AdminRole.Administrator,
            Language = language,
            ResponseNotificationEnabled = true,
        });
        return store;
    }
}
