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

        var nextRelease = _time.GetUtcNow();

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
