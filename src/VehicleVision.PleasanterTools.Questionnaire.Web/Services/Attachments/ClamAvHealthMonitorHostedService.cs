using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>DB 起動後、ClamAV の状態を定期的に確認する。</summary>
public sealed class ClamAvHealthMonitorHostedService(
    ClamAvHealthMonitor monitor,
    DatabaseStartupState startupState,
    TimeProvider timeProvider,
    ILogger<ClamAvHealthMonitorHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startupState.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await monitor.CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "ClamAV の定期確認に失敗した。間隔を置いて続ける");
                }

                await Task.Delay(CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 起動待ち・確認の間隔待ちのどちらでも正常に終了する。
        }
    }
}
