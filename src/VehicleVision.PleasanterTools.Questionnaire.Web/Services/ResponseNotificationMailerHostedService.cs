namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答通知メールの期限を定期的に確認する（Issue #357）。</summary>
public sealed class ResponseNotificationMailerHostedService(
    ResponseNotificationMailer mailer,
    ILogger<ResponseNotificationMailerHostedService> logger,
    TimeProvider timeProvider)
    : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
