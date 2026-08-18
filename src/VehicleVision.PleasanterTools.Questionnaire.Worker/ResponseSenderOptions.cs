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
