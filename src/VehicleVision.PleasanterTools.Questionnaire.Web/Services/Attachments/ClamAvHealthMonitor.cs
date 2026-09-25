using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>ClamAV の検査可否を確かめ、異常と復旧を知らせる（Issue #482）。</summary>
/// <remarks>
/// 状態はインスタンスごとに持つ。常駐サービスから直列に呼ぶ。
/// 回答や添付の代わりに固定テキストを検査する。定義の鮮度を保証する監視ではない。
/// </remarks>
public sealed class ClamAvHealthMonitor(
    IVirusScanner scanner,
    IAdminNotificationStore notifications,
    TimeProvider timeProvider,
    ILogger<ClamAvHealthMonitor> logger)
{
    private static readonly byte[] Probe = "Questionnaire ClamAV health check"u8.ToArray();
    private bool? lastHealthy;
    private AdminNotificationKind? pending;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        bool healthy;
        Exception? failure = null;
        try
        {
            healthy = await scanner.ScanAsync(Probe, cancellationToken).ConfigureAwait(false)
                == ScanVerdict.Clean;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            healthy = false;
            failure = exception;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lastHealthy != healthy)
        {
            // 初回の正常は復旧ではない。異常で起動した場合は最初から知らせる。
            if (!healthy)
            {
                logger.LogError(failure, "ClamAV の定期確認で検査できない状態を検知した");
                pending = AdminNotificationKind.ClamAvUnavailable;
            }
            else if (lastHealthy is false)
            {
                logger.LogInformation("ClamAV の定期確認で検査の復旧を確認した");
                pending = AdminNotificationKind.ClamAvRecovered;
            }

            lastHealthy = healthy;
        }

        if (pending is not { } kind)
        {
            return;
        }

        try
        {
            await notifications.RaiseAsync(
                (int)kind, Guid.Empty, timeProvider.GetUtcNow().UtcDateTime, cancellationToken)
                .ConfigureAwait(false);
            pending = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 保存できなければ次回に再試行。途中で状態が変われば最新の状態を優先する。
            logger.LogError(exception, "ClamAV の状態のお知らせを保存できなかった。次回再試行する");
        }
    }
}
