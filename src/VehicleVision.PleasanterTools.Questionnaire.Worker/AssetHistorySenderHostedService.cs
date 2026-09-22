using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

public sealed class AssetHistorySenderHostedService(
    AssetHistorySender sender,
    IAssetHistoryOutbox outbox,
    ResponseSenderOptions options,
    ILogger<AssetHistorySenderHostedService> logger,
    TimeProvider? timeProvider = null,
    DatabaseStartupState? startupState = null,
    MaintenanceMode? maintenance = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (startupState is not null)
        {
            await startupState.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
        }

        var nextRelease = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (maintenance is not null
                    && await maintenance.IsActiveAsync(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(options.IdleDelay, _time, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_time.GetUtcNow() >= nextRelease)
                {
                    await outbox.ReleaseExpiredLocksAsync(stoppingToken).ConfigureAwait(false);
                    nextRelease = _time.GetUtcNow().Add(options.ReleaseExpiredLocksInterval);
                }

                if (await sender.SendOnceAsync(stoppingToken).ConfigureAwait(false) is SendOutcome.Idle)
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
                logger.LogError(exception, "配布資料の受取履歴ワーカーで失敗が起きた");
                await Task.Delay(options.IdleDelay, _time, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
