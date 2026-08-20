using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>送信ワーカーを常駐させる。</summary>
/// <remarks>
/// <para>
/// **<c>.Web</c> に同居させる**（<c>_documents/アプリケーション設計.md</c> 8 章）。
/// Azure App Service では別プロセスを常駐させる手段が限られるため。
/// </para>
/// <para>
/// **`Always On` を有効にすること。** 無効だとアイドルで停止して送信が止まる。
/// </para>
/// </remarks>
public sealed class ResponseSenderHostedService(
    ResponseSender sender,
    IResponseOutbox outbox,
    ResponseSenderOptions options,
    ILogger<ResponseSenderHostedService> logger,
    TimeProvider? timeProvider = null)
    : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("送信ワーカーを開始した（{Worker}）", options.WorkerName);

        if (options.MaxSendsPerMinute > 0)
        {
            logger.LogInformation(
                "送信は 1 分あたり {Limit} 件までに抑える（{Interval} ミリ秒ごとに 1 件）",
                options.MaxSendsPerMinute,
                (int)options.MinSendInterval.TotalMilliseconds);
        }

        var nextRelease = _time.GetUtcNow();
        var nextSend = _time.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // **確保したまま落ちた行を戻す。** ワーカーが落ちても回答は失われない
                if (_time.GetUtcNow() >= nextRelease)
                {
                    var released = await outbox.ReleaseExpiredLocksAsync(stoppingToken)
                        .ConfigureAwait(false);
                    if (released > 0)
                    {
                        logger.LogWarning("期限切れの確保を {Count} 件解放した", released);
                    }

                    nextRelease = _time.GetUtcNow().Add(options.ReleaseExpiredLocksInterval);
                }

                // **流量に上限を掛ける**（Issue #72）。
                // **復旧直後に溜まった分を一斉送信すると Pleasanter をもう一度落とす**
                // （_documents/アーキテクチャ方針.md 10 章）
                if (options.MinSendInterval > TimeSpan.Zero)
                {
                    var wait = nextSend - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                    }

                    // **今の時刻から数える。** 前回の予定時刻に足すと、
                    // 送信が遅れたぶんを「借り」として溜め込み、後で一気に取り返してしまう
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
                // **ここで落とさない。** 1 件の失敗でワーカーを止めない
                logger.LogError(exception, "送信ワーカーで失敗が起きた。間隔を置いて続ける");
                await Task.Delay(options.IdleDelay, _time, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("送信ワーカーを停止した");
    }
}
