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

    /// <summary>添付を弾いた記録を残す日数。**0 以下にすると消さない。**（Issue #39）</summary>
    /// <remarks>
    /// <para>
    /// **管理操作の記録より短くしてある。** あちらは「誰がいつ何を変えたか」を
    /// 後から辿るためのもので、監査の都合で長く要る。
    /// **こちらは運用のための数字**（対策が効いているか、設定が厳しすぎないか）で、
    /// 半年前の値を見ることはまず無い。
    /// </para>
    /// <para>
    /// **別の設定にしてある。** 同じ値で縛ると、監査の要件に引きずられて
    /// 運用の記録まで長く持つことになる。
    /// </para>
    /// </remarks>
    public int AttachmentRejectionRetentionDays { get; init; } = 90;

    /// <summary>添付を弾いた記録を消す仕組みが働くか。</summary>
    public bool AttachmentRejectionEnabled => AttachmentRejectionRetentionDays > 0;

    /// <summary>添付を弾いた記録の保持日数の設定名。</summary>
    public const string AttachmentRejectionRetentionDaysKey =
        "QUESTIONNAIRE_ATTACHMENT_REJECTION_RETENTION_DAYS";

    /// <summary>管理者への知らせを残す日数。**0 以下にすると消さない。**（Issue #80）</summary>
    /// <remarks>
    /// ⚠️ **消えるのは既読になったものだけ**（<c>IAdminNotificationStore</c>）。
    /// 未読は日数に関わらず残る。**気付く前に消えたら、溜める意味が無い。**
    /// </remarks>
    public int NotificationRetentionDays { get; init; } = 90;

    /// <summary>管理者への知らせを消す仕組みが働くか。</summary>
    public bool NotificationEnabled => NotificationRetentionDays > 0;

    /// <summary>管理者への知らせの保持日数の設定名。</summary>
    public const string NotificationRetentionDaysKey =
        "QUESTIONNAIRE_NOTIFICATION_RETENTION_DAYS";

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

        var defaults = new AuditLogRetentionOptions();

        return new AuditLogRetentionOptions
        {
            RetentionDays = string.IsNullOrWhiteSpace(raw)
                ? defaults.RetentionDays
                : int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
            AttachmentRejectionRetentionDays = ReadDays(
                configuration,
                AttachmentRejectionRetentionDaysKey,
                defaults.AttachmentRejectionRetentionDays),
            NotificationRetentionDays = ReadDays(
                configuration,
                NotificationRetentionDaysKey,
                defaults.NotificationRetentionDays),
        };
    }

    private static int ReadDays(
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        string key,
        int fallback)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // **読めない値を黙って既定へ落とさない**（上と同じ理由）
        return int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{key} は整数で指定する（今の値: {raw}）。0 以下にすると消さない");
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
    TimeProvider? timeProvider = null,
    IAttachmentRejectionStore? rejections = null,
    IAdminNotificationStore? notifications = null)
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
        }

        if (rejections is not null && !options.AttachmentRejectionEnabled)
        {
            logger.LogWarning(
                "添付を弾いた記録を消さない設定で動いている（{Key}={Days}）。表は増え続ける",
                AuditLogRetentionOptions.AttachmentRejectionRetentionDaysKey,
                options.AttachmentRejectionRetentionDays);
        }

        // **どちらも消さないなら、常駐する意味が無い**
        if (!options.Enabled
            && !(rejections is not null && options.AttachmentRejectionEnabled)
            && !(notifications is not null && options.NotificationEnabled))
        {
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

                var now = _time.GetLocalNow().DateTime;

                if (options.Enabled)
                {
                    var threshold = now.AddDays(-options.RetentionDays);
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

                // **添付を弾いた記録も同じ周期で掃除する**（Issue #39）。
                // 常駐する係を 2 つに増やす理由が無い
                if (rejections is not null && options.AttachmentRejectionEnabled)
                {
                    var threshold = now.AddDays(-options.AttachmentRejectionRetentionDays);
                    var deleted = await rejections.DeleteOlderThanAsync(threshold, stoppingToken)
                        .ConfigureAwait(false);

                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "期限を過ぎた添付の記録を {Count} 件消した（{Threshold} より前）",
                            deleted,
                            threshold);
                    }
                }

                // **既読になった知らせも同じ周期で掃除する**（Issue #80）。
                // **未読は消えない**（ストア側が既読だけを対象にする）
                if (notifications is not null && options.NotificationEnabled)
                {
                    var threshold = now.AddDays(-options.NotificationRetentionDays);
                    var deleted = await notifications
                        .DeleteOlderThanAsync(threshold, stoppingToken)
                        .ConfigureAwait(false);

                    if (deleted > 0)
                    {
                        logger.LogInformation(
                            "期限を過ぎた既読の知らせを {Count} 件消した（{Threshold} より前）",
                            deleted,
                            threshold);
                    }
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
