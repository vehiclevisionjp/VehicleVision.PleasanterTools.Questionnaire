using System.Collections.Concurrent;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>枠の判定。</summary>
/// <param name="Allowed">通してよいか。</param>
/// <param name="RetryAfter">超えたとき、枠が空くまでの目安。</param>
public readonly record struct AdminApiTokenRateDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// 管理 API の個人トークンの枠（Issue #1027。親 #1012）。<b>認証の失敗の枠（送信元 IP ごと）</b>と、
/// <b>トークン単位の枠</b>を数える。
/// </summary>
/// <remarks>
/// <para>
/// 順序は <b>IP の枠（既存の全体の枠） → 失敗の枠（認証の前に見る） → 認証 → トークンの枠</b>。
/// 認証の前には持ち主を知らないので、トークン単位の枠は認証の後に置く。
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>失敗の枠はログインの試行の枠と別。</b>通常の API の枠に紛れさせない。
/// <b>数えるのは失敗だけ</b>（成功では増えず、戻りもしない）。見るのは認証の前（<see cref="PeekFailuresAsync"/>）、
/// 数えるのは認証の後（<see cref="RecordFailureAsync"/>）で、超えた IP の Bearer の要求は DB を引く前に <c>429</c>。
/// <b>cookie の要求は巻き込まない</b>（同じ NAT の中の画面の利用者を止めない）。
/// </description></item>
/// <item><description>
/// <b>トークン単位で恒久的に締め出さない。</b>窓が過ぎれば戻る（推測ではなく、持ち主への嫌がらせの停止になる）。
/// 読み取りと書き込みは別の枠で、書き込みを低くする。
/// </description></item>
/// <item><description>
/// 共有ストア（Redis）があれば複数台で共有する。<b>ストアに繋がらないときはプロセス内の数えへ倒す</b>
/// （既存の <see cref="SharedFixedWindowRateLimiter"/> と同じ。Core の <c>IRateLimitStore</c>＝DB の表は、
/// 要求ごとに書き込みが増えるので使わない）。プロセス内の数えは、ストアが正常でも常に進めておく
/// （ストアが落ちた瞬間に枠が空へ戻らないように）。
/// </description></item>
/// <item><description>
/// 数え先のキー（IP・トークンの識別子）も例外の内容もログへ出さない（例外は型名だけ）。
/// </description></item>
/// </list>
/// </remarks>
public sealed class AdminApiTokenRateGate(
    ISharedRateLimitStore? store,
    ILogger<AdminApiTokenRateGate> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>失敗の枠の窓。</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    /// <summary>トークン単位の枠の窓。</summary>
    public static readonly TimeSpan TokenWindow = TimeSpan.FromMinutes(1);

    private const int PurgeThreshold = 4096;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SharedStateWarning _warning = new(logger, timeProvider);
    private readonly ConcurrentDictionary<string, (long Window, int Count, long EndTicks)> _local = new();
    private readonly ConcurrentDictionary<string, long> _reported = new();

    /// <summary>この IP は、失敗の枠を使い切っているか。<b>数えない</b>（認証の前に見るだけ）。</summary>
    public async Task<AdminApiTokenRateDecision> PeekFailuresAsync(
        string ip,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var partition = FailurePartition(ip);
        if (store is not null)
        {
            try
            {
                // 数を 0 だけ足して読む。`limit - 1` 以下なら、まだ使い切っていない（`limit` 回目の失敗で締まる）
                var result = await store
                    .AcquireAsync(partition, 0, limit - 1, FailureWindow, cancellationToken)
                    .ConfigureAwait(false);
                return new AdminApiTokenRateDecision(result.Acquired, result.Acquired ? TimeSpan.Zero : result.RetryAfter);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _warning.Log(exception, "管理 API のトークンの共有レート制限の KVS に接続できないため、プロセス内の制限へ切り替えた");
            }
        }

        var (count, retryAfter) = ReadLocal(partition, FailureWindow);
        return new AdminApiTokenRateDecision(count < limit, count < limit ? TimeSpan.Zero : retryAfter);
    }

    /// <summary>認証の失敗を 1 回数える。</summary>
    public async Task RecordFailureAsync(string ip, int limit, CancellationToken cancellationToken = default)
    {
        var partition = FailurePartition(ip);
        AddLocal(partition, FailureWindow);
        if (store is null)
        {
            return;
        }

        try
        {
            await store.AcquireAsync(partition, 1, limit, FailureWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _warning.Log(exception, "管理 API のトークンの共有レート制限の KVS に接続できないため、プロセス内の制限へ切り替えた");
        }
    }

    /// <summary>トークン 1 本分の要求を 1 回数える。<b>超えたら通さない</b>（DB へ書く前に呼ぶ）。</summary>
    /// <param name="tokenId">トークンの識別子。</param>
    /// <param name="write">書き込み（<c>GET</c>・<c>HEAD</c>・<c>OPTIONS</c> 以外）か。</param>
    /// <param name="limit">窓（1 分）あたりの回数。</param>
    public async Task<AdminApiTokenRateDecision> AcquireTokenAsync(
        Guid tokenId,
        bool write,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var partition = $"admin-token:{(write ? "write" : "read")}:{tokenId:N}";
        var (localCount, localRetryAfter) = AddLocal(partition, TokenWindow);
        if (store is not null)
        {
            try
            {
                var result = await store
                    .AcquireAsync(partition, 1, limit, TokenWindow, cancellationToken)
                    .ConfigureAwait(false);
                return new AdminApiTokenRateDecision(result.Acquired, result.Acquired ? TimeSpan.Zero : result.RetryAfter);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _warning.Log(exception, "管理 API のトークンの共有レート制限の KVS に接続できないため、プロセス内の制限へ切り替えた");
            }
        }

        return new AdminApiTokenRateDecision(localCount <= limit, localCount <= limit ? TimeSpan.Zero : localRetryAfter);
    }

    /// <summary>
    /// この窓で、同じ種類の記録をまだしていなければ <c>true</c>（してあれば <c>false</c>）。
    /// <b>枠を超えた要求のたびに監査へ書くと、枠が書き込みの増幅になる</b>ので、窓ごとに 1 回へ間引く。
    /// </summary>
    public bool TryMarkReported(string key, TimeSpan window)
    {
        var current = WindowOf(window);
        var now = _time.GetUtcNow().UtcTicks;
        if (_reported.Count > PurgeThreshold)
        {
            foreach (var pair in _reported)
            {
                if (pair.Value < now)
                {
                    _reported.TryRemove(pair);
                }
            }
        }

        var end = (current + 1) * window.Ticks;
        var updated = false;
        _reported.AddOrUpdate(
            key,
            _ =>
            {
                updated = true;
                return end;
            },
            (_, previousEnd) =>
            {
                if (previousEnd > now)
                {
                    return previousEnd;
                }

                updated = true;
                return end;
            });
        return updated;
    }

    private static string FailurePartition(string ip) => $"admin-token-fail:{ip}";

    private long WindowOf(TimeSpan window) => _time.GetUtcNow().UtcTicks / window.Ticks;

    private (int Count, TimeSpan RetryAfter) ReadLocal(string partition, TimeSpan window)
    {
        var current = WindowOf(window);
        var retryAfter = RetryAfterOf(current, window);
        return _local.TryGetValue(partition, out var entry) && entry.Window == current
            ? (entry.Count, retryAfter)
            : (0, retryAfter);
    }

    private (int Count, TimeSpan RetryAfter) AddLocal(string partition, TimeSpan window)
    {
        var current = WindowOf(window);
        var now = _time.GetUtcNow().UtcTicks;
        if (_local.Count > PurgeThreshold)
        {
            foreach (var pair in _local)
            {
                if (pair.Value.EndTicks < now)
                {
                    _local.TryRemove(pair);
                }
            }
        }

        var end = (current + 1) * window.Ticks;
        var updated = _local.AddOrUpdate(
            partition,
            _ => (current, 1, end),
            (_, previous) => previous.Window == current ? (current, previous.Count + 1, end) : (current, 1, end));
        return (updated.Count, RetryAfterOf(current, window));
    }

    private TimeSpan RetryAfterOf(long currentWindow, TimeSpan window)
    {
        var next = (currentWindow + 1) * window.Ticks;
        var remaining = TimeSpan.FromTicks(next - _time.GetUtcNow().UtcTicks);
        return remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
    }
}
