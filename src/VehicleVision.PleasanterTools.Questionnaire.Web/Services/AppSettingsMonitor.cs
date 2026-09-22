using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>DB のアプリケーション設定を定期的に読み直し、実行中の処理へ反映する。</summary>
public sealed class AppSettingsMonitor(
    IAppSettingsProvider provider,
    IConfiguration configuration,
    DatabaseStartupState startupState,
    ILogger<AppSettingsMonitor> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly Lock gate = new();
    private readonly List<Action<AppSettingsSnapshot>> listeners = [];
    private AppSettingsSnapshot current = AppSettingsProvider.InitialSnapshot(configuration);

    public string this[string key] => Volatile.Read(ref current)[key];

    public int GetInt32(string key) =>
        int.Parse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>設定変更時の処理を登録し、現在値も直ちに渡す。</summary>
    public void Register(Action<AppSettingsSnapshot> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate)
        {
            listeners.Add(listener);
            listener(current);
        }
    }

    /// <summary>管理画面で保存した値を、同じプロセスへ直ちに反映する。</summary>
    public void Apply(AppSettingsSnapshot snapshot)
    {
        Action<AppSettingsSnapshot>[] currentListeners;
        lock (gate)
        {
            Volatile.Write(ref current, snapshot);
            currentListeners = [.. listeners];
        }

        foreach (var listener in currentListeners)
        {
            listener(snapshot);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await startupState.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Apply(await provider.GetAsync(stoppingToken).ConfigureAwait(false));
                await Task.Delay(
                    AppSettingsProvider.CacheLifetime,
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "アプリケーション設定を再読み込みできなかった。次の周期で再試行する");
                await Task.Delay(
                    AppSettingsProvider.CacheLifetime,
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
