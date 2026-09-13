using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>送信待ちのメールを 1 件ずつ送る（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **回答の送信（<see cref="ResponseSender"/>）と同じ構造。**
/// 確保 → 送る → 送れたら消す／駄目なら指数バックオフ／上限でデッドレター。
/// **2 通りの流儀を覚えなくて済むように、わざと揃えてある。**
/// </para>
/// <para>
/// ⚠️ **宛先・件名・本文をログへ出さない。** 回答者のメールアドレスは、
/// 完全匿名を前提にした本アプリで唯一、個人を指す値になり得る。
/// </para>
/// <para>
/// ⚠️ **メールが届かなくても、回答は Pleasanter に入っている。**
/// 知らせの種類を回答のデッドレターと分けているのはそのため
/// （<see cref="AdminNotificationKind.MailDeadLettered"/>）。
/// </para>
/// </remarks>
public sealed class MailSender(
    IMailOutbox outbox,
    IMailTransport transport,
    IMailPayloadProtector protector,
    MailSenderOptions options,
    ILogger<MailSender> logger,
    TimeProvider? timeProvider = null,
    IAdminNotificationStore? notifications = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>1 件だけ処理する。</summary>
    public async Task<SendOutcome> SendOnceAsync(CancellationToken cancellationToken = default)
    {
        var claimed = await outbox
            .ClaimAsync(options.WorkerName, options.LockDuration, cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return SendOutcome.Idle;
        }

        try
        {
            return await ProcessAsync(claimed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // **確保したまま落ちても、期限切れで解放される。** メールは失われない
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "メールの送信で想定外の失敗が起きた");
            return await RescheduleAsync(claimed, "想定外の失敗", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SendOutcome> ProcessAsync(
        PendingMail claimed,
        CancellationToken cancellationToken)
    {
        var mail = protector.Unprotect(claimed.PayloadProtected);
        if (mail is null)
        {
            // **鍵が変わった・行が壊れた。** 何度やっても読めないので分離する。
            // ⚠️ **鍵を失うと送れない**のは 2 要素の共有鍵と同じ（設計どおり）
            return await DeadLetterAsync(
                claimed, "送信待ちの中身を復号できない（鍵の変更か破損）", cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await transport.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        catch (MailDeliveryException exception)
        {
            // **再送する価値があるかは送信経路が判定済み**（4xx は一時、5xx は恒久）
            return exception.IsTransient
                ? await RescheduleAsync(claimed, exception.Message, cancellationToken)
                    .ConfigureAwait(false)
                : await DeadLetterAsync(claimed, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
        }

        // **送れたら消す。** 宛先も本文も残さない
        await outbox.CompleteAsync(claimed.MailId, cancellationToken).ConfigureAwait(false);
        return SendOutcome.Sent;
    }

    private async Task<SendOutcome> RescheduleAsync(
        PendingMail claimed,
        string error,
        CancellationToken cancellationToken)
    {
        if (claimed.RetryCount >= options.MaxRetryCount)
        {
            return await DeadLetterAsync(
                claimed,
                $"再送の上限に達した（{claimed.RetryCount} 回）: {error}",
                cancellationToken).ConfigureAwait(false);
        }

        var next = options.NextAttemptAt(_time.GetUtcNow().UtcDateTime, claimed.RetryCount);
        await outbox
            .RescheduleAsync(claimed.MailId, next, error, cancellationToken)
            .ConfigureAwait(false);
        return SendOutcome.Rescheduled;
    }

    private async Task<SendOutcome> DeadLetterAsync(
        PendingMail claimed,
        string error,
        CancellationToken cancellationToken)
    {
        // **黙って溜め続けない。** 通知の対象
        logger.LogError("メールをデッドレターへ回した: {Reason}", error);
        await outbox.DeadLetterAsync(claimed.MailId, error, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(claimed.SurveyId, cancellationToken).ConfigureAwait(false);
        return SendOutcome.DeadLettered;
    }

    /// <summary>管理者への知らせを 1 件立てる。</summary>
    /// <remarks>
    /// ⚠️ **知らせを書けなくても送信の結果を変えない**（<see cref="ResponseSender"/> と同じ）。
    /// **宛先も本文も渡していない**（種類とアンケートだけ）。
    /// </remarks>
    private async Task NotifyAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        if (notifications is null)
        {
            return;
        }

        try
        {
            await notifications
                .RaiseAsync(
                    (int)AdminNotificationKind.MailDeadLettered,
                    surveyId,
                    _time.GetUtcNow().UtcDateTime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "管理者への知らせを書けなかった（メールのデッドレター）");
        }
    }
}
