using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>管理操作の記録をいつまで残すか。</summary>
/// <remarks>
/// <para>
/// **消さないと増え続ける**（<c>_documents/データモデル設計.md</c>）。
/// 監査ログは 1 操作 1 行で、画面から読むたびに大きくなった表を走査することになる。
/// </para>
/// <para>
/// **既定は消さない側にしない。** 残す期間を決めないまま運用が始まると、
/// 「消してよいか分からない」まま増え続ける。
/// </para>
/// </remarks>
public sealed class AuditLogRetentionOptions
{
    /// <summary>設定の名前。</summary>
    public const string RetentionDaysKey = "QUESTIONNAIRE_AUDITLOG_RETENTION_DAYS";

    /// <summary>残す日数。**0 以下にすると消さない。**</summary>
    /// <remarks>
    /// **1 年。** 「去年の今ごろ誰が何をしたか」を追えて、かつ無限には増えない長さ。
    /// 法令や社内規程で別に決まっているなら、そちらに合わせて上書きすること。
    /// </remarks>
    public int RetentionDays { get; init; } = 365;

    /// <summary>掃除する間隔。</summary>
    /// <remarks>**頻繁に走らせない。** 消す対象は 1 日で大きく変わらない。</remarks>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>消す仕組みが働くか。</summary>
    public bool Enabled => RetentionDays > 0;

    /// <summary>設定から読む。</summary>
    public static AuditLogRetentionOptions FromConfiguration(
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[RetentionDaysKey];

        // **読めない値を黙って既定へ落とさない。** 設定したつもりが効いていない状態を作る
        if (!string.IsNullOrWhiteSpace(raw) && !int.TryParse(raw, out _))
        {
            throw new InvalidOperationException(
                $"{RetentionDaysKey} は整数で指定する（今の値: {raw}）。"
                + "0 以下にすると消さない");
        }

        return string.IsNullOrWhiteSpace(raw)
            ? new AuditLogRetentionOptions()
            : new AuditLogRetentionOptions { RetentionDays = int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) };
    }
}

/// <summary>期限を過ぎた管理操作の記録を消す。</summary>
/// <remarks>
/// <para>
/// **送信ワーカーと分ける。** 回答の送信が滞っている間も掃除は進んでよいし、
/// 掃除が失敗しても送信は止めない。
/// </para>
/// <para>
/// **起動直後には走らせない。** 立ち上がりに DB を掴むと、
/// スケールアウトした全インスタンスが同時に大きな DELETE を投げる。
/// </para>
/// </remarks>
public sealed class AuditLogRetentionService(
    IAuditLogStore store,
    AuditLogRetentionOptions options,
    ILogger<AuditLogRetentionService> logger,
    TimeProvider? timeProvider = null)
    : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            // **黙って何もしない、にはしない。** 増え続ける設定で動いていると分かるようにする
            logger.LogWarning(
                "管理操作の記録を消さない設定で動いている（{Key}={Days}）。表は増え続ける",
                AuditLogRetentionOptions.RetentionDaysKey,
                options.RetentionDays);
            return;
        }

        logger.LogInformation(
            "管理操作の記録は {Days} 日残す（{Interval} ごとに掃除する）",
            options.RetentionDays,
            options.SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // **先に待つ。** 立ち上がり直後に全インスタンスが一斉に消しに行かないように
                await Task.Delay(options.SweepInterval, _time, stoppingToken).ConfigureAwait(false);

                var threshold = _time.GetLocalNow().DateTime.AddDays(-options.RetentionDays);
                var deleted = await store.DeleteOlderThanAsync(threshold, stoppingToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    // **消したことは残す。** 監査ログが減った理由が分からないと、
                    // 「消えている」と「消した」の区別が付かない
                    logger.LogInformation(
                        "期限を過ぎた管理操作の記録を {Count} 件消した（{Threshold} より前）",
                        deleted,
                        threshold);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // **ここで落とさない。** 掃除の失敗でアプリを止めない
                logger.LogError(exception, "管理操作の記録を消せなかった。次の周期で試す");
            }
        }
    }
}
