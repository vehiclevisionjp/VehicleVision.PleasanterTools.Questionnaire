namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>メールを送れる状態かを、設定の書き間違いがあっても例外にせずに調べる（Issue #864）。</summary>
/// <remarks>
/// 管理画面のログイン画面が使う <c>GET /api/admin/session</c> から呼ぶ。
/// **メール設定が組み立てられないだけで管理者がログインできなくなる**のを避けるため、
/// 組み立てに失敗したときは「送れない」として扱い、理由を警告へ出す。
/// 設定が直るまで実際の送信は今までどおり失敗する。
/// </remarks>
public static class MailReadiness
{
    public static async Task<bool> IsReadyAsync(
        IMailSettingsProvider settings,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return (await settings.GetAsync(cancellationToken).ConfigureAwait(false)).IsReady;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                "メール設定を読めないため、メールは使えない状態として扱う: {Reason}",
                exception.Message);
            return false;
        }
    }
}
