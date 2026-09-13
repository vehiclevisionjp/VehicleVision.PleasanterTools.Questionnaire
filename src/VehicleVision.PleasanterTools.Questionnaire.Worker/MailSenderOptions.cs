using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>メール送信ワーカーの設定（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **回答の送信（<see cref="ResponseSenderOptions"/>）と分けてある。**
/// 相手も、詰まったときの意味も違う。回答が遅れるのは**データが届かないこと**だが、
/// メールが遅れるのは**知らせが遅れること**で、急ぎ方が同じではない。
/// </para>
/// <para>
/// ⚠️ **メールの送信元にはたいてい流量の上限がある**（Azure Communication Services の
/// 既定はとくに低い）。上限を超えると 4xx で弾かれ、再送で余計に混む。
/// **既定を控えめにしてある。**
/// </para>
/// </remarks>
public sealed class MailSenderOptions
{
    /// <summary>このワーカーを識別する名前。確保した行に記録される。</summary>
    public string WorkerName { get; init; } = Environment.MachineName;

    /// <summary>1 件を確保しておく時間。**これを過ぎたら他のワーカーが拾える。**</summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>再送の基準間隔。**指数バックオフの底。**</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>再送間隔の上限。</summary>
    /// <remarks>
    /// **回答の送信より長く取ってある。** メールの相手が落ちているときに
    /// 短い間隔で叩き続けても、こちらが詰まるだけで早くは届かない。
    /// </remarks>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>これを超えたらデッドレターへ回す。**管理者へ通知すること。**</summary>
    public int MaxRetryCount { get; init; } = 10;

    /// <summary>送るものが無いときに次を見るまでの間隔。</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>期限切れの確保を解放する間隔。</summary>
    public TimeSpan ReleaseExpiredLocksInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>1 分あたりに送る件数の上限。**0 以下で無制限。**</summary>
    /// <remarks>
    /// ⚠️ **上限はインスタンスごと。** スケールアウトすると台数ぶん増える。
    /// 送信元の上限に合わせて割り算して設定すること。
    /// </remarks>
    public int MaxSendsPerMinute { get; init; } = 60;

    /// <summary>送信と送信の間に最低限あける時間。</summary>
    /// <remarks>**まとめて撃たせない**（回答の送信と同じ理由）。</remarks>
    public TimeSpan MinSendInterval => MaxSendsPerMinute > 0
        ? TimeSpan.FromTicks(TimeSpan.TicksPerMinute / MaxSendsPerMinute)
        : TimeSpan.Zero;

    /// <summary>1 分あたりの上限の設定名。</summary>
    public const string MaxSendsPerMinuteKey = "QUESTIONNAIRE_MAIL_SEND_PER_MINUTE";

    /// <summary>設定から読む。</summary>
    /// <remarks>**読めない値を黙って既定へ落とさない**（他の設定と同じ）。</remarks>
    public static MailSenderOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[MaxSendsPerMinuteKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new MailSenderOptions();
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? new MailSenderOptions { MaxSendsPerMinute = value }
            : throw new InvalidOperationException(
                $"{MaxSendsPerMinuteKey} は整数で指定する（今の値: {raw}）。0 以下で無制限");
    }

    /// <summary>指数バックオフで次に送る時刻を決める。</summary>
    public DateTime NextAttemptAt(DateTime utcNow, int retryCount)
    {
        var factor = Math.Pow(2, Math.Min(retryCount, 16));
        var delay = TimeSpan.FromTicks((long)Math.Min(
            RetryBaseDelay.Ticks * factor,
            RetryMaxDelay.Ticks));
        return utcNow.Add(delay);
    }
}
