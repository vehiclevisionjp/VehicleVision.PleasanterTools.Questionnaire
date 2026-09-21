using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>新しい回答を設定した間隔でまとめ、希望した管理者へ知らせる（Issue #357）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **回答本文は受け取らない。** 件数・時刻・アンケート名だけでメールを作る。
/// </para>
/// <para>
/// メールと集約済み件数は同じ DB トランザクションで確定する。
/// 複数インスタンスが同時に動いても、同じ集約を二重に積まない。
/// </para>
/// </remarks>
public sealed class ResponseNotificationMailer(
    IResponseNotificationStore notifications,
    IAdminUserStore users,
    IMailPayloadProtector protector,
    ILogger<ResponseNotificationMailer> logger,
    ResponseNotificationMailerOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>期限を迎えたまとめ通知を送信待ちへ積む。</summary>
    /// <returns>処理を確定したアンケート数。</returns>
    public async Task<int> QueueDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var due = await notifications
            .ListDueResponseDigestsAsync(now.Subtract(options.DigestInterval), cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return 0;
        }

        var recipients = await users
            .ListResponseNotificationRecipientsAsync(cancellationToken)
            .ConfigureAwait(false);
        var queued = 0;

        foreach (var digest in due)
        {
            try
            {
                var mails = recipients
                    .Where(recipient => MailAddress.TryCreate(recipient.LoginId, out _))
                    .Select(recipient => Compose(digest, recipient))
                    .ToList();

                if (await notifications
                    .TryQueueResponseDigestAsync(digest, mails, now, cancellationToken)
                    .ConfigureAwait(false))
                {
                    queued++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // **次の巡回で再試行する。** 集約済み件数とメールは同じ取引なので、
                // 途中まで積まれた状態にはならない
                logger.LogError(
                    exception,
                    "回答通知メールを積めなかった（SurveyId={SurveyId}）",
                    digest.SurveyId);
            }
        }

        return queued;
    }

    private ProtectedResponseNotificationMail Compose(
        ResponseNotificationDigest digest,
        ResponseNotificationRecipient recipient)
    {
        var language = recipient.Language;
        var title = string.IsNullOrWhiteSpace(digest.SurveyTitle)
            ? ServerMessages.Get(ServerMessageKeys.RemovedSurvey, language)
            : digest.SurveyTitle;
        var count = digest.Count - digest.MailQueuedCount;
        var from = digest.LastMailQueuedAt ?? digest.FirstOccurredAt;
        var mail = new OutgoingMail(
            recipient.LoginId,
            ServerMessages.Get(ServerMessageKeys.ResponseNotificationMailSubject, language),
            ServerMessages.Get(
                ServerMessageKeys.ResponseNotificationMailBody,
                language,
                title,
                count,
                FormatUtc(from),
                FormatUtc(digest.LastOccurredAt)));

        return new ProtectedResponseNotificationMail(
            MailIdOf(digest.SurveyId, recipient.AdminUserId, digest.Count),
            protector.Protect(mail));
    }

    private static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>通知・受取人・集約後件数から、同じまとめに同じ識別子を割り当てる。</summary>
    public static Guid MailIdOf(Guid surveyId, Guid adminUserId, int count)
    {
        var source = $"response-notification:{surveyId:N}:{adminUserId:N}:{count}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return new Guid(hash.AsSpan(0, 16));
    }
}
