using System.Net.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

public enum AutoReplyTestMailOutcome
{
    Queued,
    LoginIdNotEmail,
    MailDisabled,
    QueueFailed,
}

/// <summary>保存前の完了メールを、管理者本人へ試し送信する（Issue #319）。</summary>
public sealed class AutoReplyTestMailer(
    IMailOutbox outbox,
    IMailPayloadProtector protector,
    IMailSettingsProvider mailSettings,
    PleasanterOptions pleasanter,
    TimeProvider timeProvider,
    ILogger<AutoReplyTestMailer> logger)
{
    public AutoReplyTestMailer(
        IMailOutbox outbox,
        IMailPayloadProtector protector,
        MailOptions options,
        PleasanterOptions pleasanter,
        TimeProvider timeProvider,
        ILogger<AutoReplyTestMailer> logger)
        : this(
            outbox,
            protector,
            new FixedMailSettingsProvider(options),
            pleasanter,
            timeProvider,
            logger)
    {
    }

    public async Task<AutoReplyTestMailOutcome> TryEnqueueAsync(
        SurveyDefinition definition,
        string? language,
        string? loginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!MailAddress.TryCreate(loginId, out _))
        {
            return AutoReplyTestMailOutcome.LoginIdNotEmail;
        }

        var options = await mailSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!options.IsReady)
        {
            return AutoReplyTestMailOutcome.MailDisabled;
        }

        try
        {
            var preview = AdminAutoReplyEndpoints.Preview(
                definition,
                language,
                options,
                pleasanter,
                timeProvider.GetUtcNow());
            var mail = new OutgoingMail(
                loginId,
                ServerMessages.Get(ServerMessageKeys.AutoReplyTestSubjectPrefix, language)
                    + preview.Subject,
                preview.Body,
                preview.FromName,
                preview.ReplyToAddress,
                preview.BccAddress);
            var surveyId = Guid.TryParse(definition.SurveyId, out var parsed)
                ? parsed
                : Guid.Empty;

            var queued = await outbox.EnqueueAsync(
                Guid.NewGuid(),
                (int)MailKind.AutoReplyTest,
                surveyId,
                protector.Protect(mail),
                cancellationToken).ConfigureAwait(false);
            return queued
                ? AutoReplyTestMailOutcome.Queued
                : AutoReplyTestMailOutcome.QueueFailed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "完了メールの試し送信を送信待ちへ積めなかった");
            return AutoReplyTestMailOutcome.QueueFailed;
        }
    }
}
