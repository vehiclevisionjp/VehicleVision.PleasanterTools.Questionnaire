using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答通知メールの期限を定期的に確認する（Issue #357）。</summary>
public sealed class ResponseNotificationMailerHostedService(
    ResponseNotificationMailer mailer,
    ILogger<ResponseNotificationMailerHostedService> logger,
    TimeProvider timeProvider,
    DatabaseStartupState? startupState = null)
    : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ⚠️ **スキーマが当たるまで待つ**（Issue #404。ほかのワーカーと同じ作り）。
        // `app.StartAsync()` はマイグレーションより前に走るので、待たずに DB を読むと
        // **空の DB への初回起動で必ず「テーブルが無い」と失敗を記録する。**
        // 間隔を置いて直るとはいえ、起動のたびに失敗が出ていては本当の異常と見分けがつかない。
        if (startupState is not null)
        {
            await startupState.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
        }

        logger.LogInformation("回答通知メールの集約ワーカーを開始した");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await mailer.QueueDueAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "回答通知メールの集約に失敗した。間隔を置いて続ける");
                await Task.Delay(CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("回答通知メールの集約ワーカーを停止した");
    }
}
