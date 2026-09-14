using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>メール送信ワーカーを常駐させる（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **<c>.Web</c> に同居させる**（回答の送信ワーカーと同じ。
/// <c>_documents/アプリケーション設計.md</c> 8 章）。
/// </para>
/// <para>
/// **メールが無効なら登録しない。** 何も送らないループを回し続けない。
/// </para>
/// <para>
/// ⚠️ **回答の送信ワーカーとは別に動く。** メールが詰まっても回答は送られ、
/// **回答が詰まってもメールは出る。** どちらかの不調がもう一方を止めない。
/// </para>
/// </remarks>
public sealed class MailSenderHostedService(
    MailSender sender,
    IMailOutbox outbox,
    MailSenderOptions options,
    ILogger<MailSenderHostedService> logger,
    TimeProvider? timeProvider = null)
    : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("メール送信ワーカーを開始した（{Worker}）", options.WorkerName);

        if (options.MaxSendsPerMinute > 0)
        {
            logger.LogInformation(
                "メールは 1 分あたり {Limit} 通までに抑える", options.MaxSendsPerMinute);
        }

        var nextRelease = _time.GetUtcNow();
        var nextSend = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // **確保したまま落ちた行を戻す。** ワーカーが落ちてもメールは失われない
                if (_time.GetUtcNow() >= nextRelease)
                {
                    var released = await outbox.ReleaseExpiredLocksAsync(stoppingToken)
                        .ConfigureAwait(false);
                    if (released > 0)
                    {
                        logger.LogWarning("期限切れの確保を {Count} 件解放した（メール）", released);
                    }

                    nextRelease = _time.GetUtcNow().Add(options.ReleaseExpiredLocksInterval);
                }

                // **送信元の流量の上限を超えない。** 超えると弾かれ、再送で余計に混む
                if (options.MinSendInterval > TimeSpan.Zero)
                {
                    var wait = nextSend - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                    }

                    // **今の時刻から数える**（遅れを「借り」として溜め込まない）
                    nextSend = _time.GetUtcNow().Add(options.MinSendInterval);
                }

                var outcome = await sender.SendOnceAsync(stoppingToken).ConfigureAwait(false);

                if (outcome is SendOutcome.Idle)
                {
                    await Task.Delay(options.IdleDelay, _time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // **ここで落とさない。** 1 通の失敗でワーカーを止めない
                logger.LogError(exception, "メール送信ワーカーで失敗が起きた。間隔を置いて続ける");
                await Task.Delay(options.IdleDelay, _time, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("メール送信ワーカーを停止した");
    }
}
