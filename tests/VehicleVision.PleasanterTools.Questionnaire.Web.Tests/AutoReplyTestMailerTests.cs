using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>完了メールを管理者本人へ試し送信する（Issue #319）。</summary>
public class AutoReplyTestMailerTests
{
    private sealed class FakeOutbox : IMailOutbox
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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeadLetterAsync(
            Guid mailId, string error, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ReleaseExpiredLocksAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<MailOutboxStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailOutboxStatus(Enqueued.Count, null, 0));
    }

    private sealed class FakeProtector : IMailPayloadProtector
    {
        public OutgoingMail? Protected { get; private set; }

        public string Protect(OutgoingMail mail)
        {
            Protected = mail;
            return "protected";
        }

        public OutgoingMail? Unprotect(string protectedPayload) => Protected;
    }

    private static readonly Guid SurveyId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SurveyDefinition Definition() => new()
    {
        SurveyId = SurveyId.ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("満足度調査"),
        AutoReply = new AutoReplySettings
        {
            Enabled = true,
            ToQuestionId = "mail",
            Subject = LocalizedText.Japanese("{{title}} の受付"),
            Body = LocalizedText.Japanese("{{submittedAt}}\n{{answers}}\n{{formUrl}}"),
        },
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
                        Title = LocalizedText.Japanese("メール"),
                        Settings = new QuestionSettings { Format = TextFormat.Email },
                    },
                ],
            },
        ],
    };

    private static (
        AutoReplyTestMailer Mailer,
        FakeOutbox Outbox,
        FakeProtector Protector) Create()
    {
        var outbox = new FakeOutbox();
        var protector = new FakeProtector();
        var mailer = new AutoReplyTestMailer(
            outbox,
            protector,
            new MailOptions
            {
                Enabled = true,
                Host = "smtp.example.test",
                FromAddress = "noreply@example.test",
                BaseUrl = "https://survey.example.test",
            },
            new PleasanterOptions
            {
                BaseUrl = "https://pleasanter.example.test",
                ApiKey = "test",
                ApiKeyUserTimeZoneId = "UTC",
            },
            new FakeTimeProvider(
                new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<AutoReplyTestMailer>.Instance);
        return (mailer, outbox, protector);
    }

    [Fact]
    public async Task ログインIDがメールアドレスなら専用の種類で送信待ちへ積む()
    {
        var (mailer, outbox, _) = Create();

        var outcome = await mailer.TryEnqueueAsync(
            Definition(), "ja", "admin@example.test");

        Assert.Equal(AutoReplyTestMailOutcome.Queued, outcome);
        var entry = Assert.Single(outbox.Enqueued);
        Assert.Equal((int)MailKind.AutoReplyTest, entry.Kind);
        Assert.Equal(SurveyId, entry.SurveyId);
        Assert.Equal("protected", entry.Payload);
    }

    [Fact]
    public async Task ログインIDがメールアドレスでなければ理由を返して積まない()
    {
        var (mailer, outbox, _) = Create();

        var outcome = await mailer.TryEnqueueAsync(
            Definition(), "ja", "administrator");

        Assert.Equal(AutoReplyTestMailOutcome.LoginIdNotEmail, outcome);
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task 宛先は渡されたログイン中の管理者自身になる()
    {
        var (mailer, _, protector) = Create();

        await mailer.TryEnqueueAsync(Definition(), "ja", "me@example.test");

        Assert.NotNull(protector.Protected);
        Assert.Equal("me@example.test", protector.Protected.ToAddress);
        Assert.StartsWith("【試し送信】", protector.Protected.Subject, StringComparison.Ordinal);
        Assert.Contains("満足度調査", protector.Protected.Subject, StringComparison.Ordinal);
        Assert.Contains("2026-09-17 00:00", protector.Protected.Body, StringComparison.Ordinal);
        Assert.Contains("https://survey.example.test/f/preview", protector.Protected.Body, StringComparison.Ordinal);
    }
}
