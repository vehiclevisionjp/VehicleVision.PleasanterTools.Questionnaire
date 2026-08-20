using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Worker;

/// <summary>送信ワーカーの設定。</summary>
public sealed class ResponseSenderOptions
{
    /// <summary>このワーカーを識別する名前。確保した行に記録される。</summary>
    public string WorkerName { get; init; } = Environment.MachineName;

    /// <summary>1 件を確保しておく時間。**これを過ぎたら他のワーカーが拾える。**</summary>
    /// <remarks>
    /// 短すぎると送信中の行を横取りされ、長すぎるとワーカーが落ちたときの復帰が遅れる。
    /// </remarks>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>再送の基準間隔。**指数バックオフの底。**</summary>
    /// <remarks>
    /// **Pleasanter 復旧直後に溜まった分を一斉送信すると再び落とす**
    /// （<c>_documents/アーキテクチャ方針.md</c> 10 章）。
    /// </remarks>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>再送間隔の上限。</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>これを超えたらデッドレターへ回す。**管理者へ通知すること。**</summary>
    public int MaxRetryCount { get; init; } = 20;

    /// <summary>送るものが無いときに次を見るまでの間隔。</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>期限切れの確保を解放する間隔。</summary>
    public TimeSpan ReleaseExpiredLocksInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>1 分あたりに送る件数の上限。**0 以下で無制限。**（Issue #72）</summary>
    /// <remarks>
    /// <para>
    /// **復旧直後に溜まった分を一斉に送ると、Pleasanter をもう一度落とす**
    /// （<c>_documents/アーキテクチャ方針.md</c> 10 章）。
    /// 指数バックオフは 1 件ずつの再送を散らすだけで、**総量は抑えない。**
    /// </para>
    /// <para>
    /// **同時送信数は構造として 1。** ワーカーは 1 件確保して送り終えてから次を取る
    /// （<c>ResponseSenderHostedService</c>）。並列に送る口をわざと作っていない。
    /// </para>
    /// <para>
    /// ⚠️ **上限はインスタンスごと。** スケールアウトすると台数ぶん増える。
    /// Pleasanter 側の余力に合わせて割り算して設定すること。
    /// </para>
    /// </remarks>
    public int MaxSendsPerMinute { get; init; } = 600;

    /// <summary>送信と送信の間に最低限あける時間。</summary>
    /// <remarks>
    /// **まとめて撃たせない。** 「1 分に N 件」を許すと、
    /// 分の頭に N 件を一気に投げても条件を満たしてしまう。
    /// **等間隔に均す**方が、受ける側にとって扱いやすい。
    /// </remarks>
    public TimeSpan MinSendInterval => MaxSendsPerMinute > 0
        ? TimeSpan.FromTicks(TimeSpan.TicksPerMinute / MaxSendsPerMinute)
        : TimeSpan.Zero;

    /// <summary>1 分あたりの上限の設定名。</summary>
    public const string MaxSendsPerMinuteKey = "QUESTIONNAIRE_SEND_PER_MINUTE";

    /// <summary>設定から読む。</summary>
    /// <remarks>**読めない値を黙って既定へ落とさない。** 設定したつもりが効かない状態を作る。</remarks>
    public static ResponseSenderOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[MaxSendsPerMinuteKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResponseSenderOptions();
        }

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? new ResponseSenderOptions { MaxSendsPerMinute = value }
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
