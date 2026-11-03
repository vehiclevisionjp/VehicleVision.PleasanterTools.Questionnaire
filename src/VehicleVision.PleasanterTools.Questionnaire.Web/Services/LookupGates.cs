using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>枠の判定結果。</summary>
public enum LookupRateOutcome
{
    /// <summary>通してよい。</summary>
    Allowed,

    /// <summary>枠を超えた（429）。</summary>
    Limited,

    /// <summary>共有ストアの障害で数えられない。**拒否側へ倒す**（503）。</summary>
    Unavailable,
}

/// <summary>枠の判定。</summary>
public readonly record struct LookupRateDecision(LookupRateOutcome Outcome, TimeSpan RetryAfter);

/// <summary>ルックアップの公開 API の枠（Issue #832・仕様書 6.2 節）。</summary>
public interface ILookupRateGate
{
    /// <summary>1 回分を数える。</summary>
    /// <param name="scope">枠の種類（回答者・IP・フォームなど）。</param>
    /// <param name="key">数え先。ログへ出さない。</param>
    Task<LookupRateDecision> AcquireAsync(
        string scope,
        string key,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}

/// <summary>既存の共有レート制限ストアを再利用した枠。</summary>
/// <remarks>
/// <para>
/// 共有ストアがあるときは複数インスタンスで枠を共有する。**ストアの障害時は
/// プロセス内の枠へ退避せず拒否する**（既存の <see cref="SharedFixedWindowRateLimiter"/> と違い、
/// 退避すると枠が緩むため。仕様書 6.2 節）。ストアが構成されていない単一構成では、プロセス内の固定窓で数える。
/// </para>
/// <para>数え先のキーも例外の内容もログへ出さない（例外は型名だけ）。</para>
/// </remarks>
public sealed class LookupRateGate(ISharedRateLimitStore? store, ILogger<LookupRateGate> logger)
    : ILookupRateGate, IDisposable
{
    private readonly ConcurrentDictionary<string, PartitionedRateLimiter<string>> local = new();

    public async Task<LookupRateDecision> AcquireAsync(
        string scope,
        string key,
        int limit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return AcquireLocal(scope, key, limit, window);
        }

        try
        {
            var result = await store
                .AcquireAsync($"lookup:{scope}:{key}", 1, limit, window, cancellationToken)
                .ConfigureAwait(false);
            return result.Acquired
                ? new LookupRateDecision(LookupRateOutcome.Allowed, TimeSpan.Zero)
                : new LookupRateDecision(LookupRateOutcome.Limited, result.RetryAfter);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "ルックアップの共有レート制限ストアに接続できないため拒否した（種類={ExceptionType}）",
                exception.GetType().Name);
            return new LookupRateDecision(LookupRateOutcome.Unavailable, TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        foreach (var limiter in local.Values)
        {
            limiter.Dispose();
        }

        local.Clear();
    }

    private LookupRateDecision AcquireLocal(string scope, string key, int limit, TimeSpan window)
    {
        var limiter = local.GetOrAdd(
            $"{scope}|{limit}|{window.Ticks}",
            _ => PartitionedRateLimiter.Create<string, string>(partitionKey =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = window,
                        QueueLimit = 0,
                    })));
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
        {
            return new LookupRateDecision(LookupRateOutcome.Allowed, TimeSpan.Zero);
        }

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : window;
        return new LookupRateDecision(LookupRateOutcome.Limited, retryAfter);
    }
}

/// <summary>フォームごとの同時検索数を制限する（Issue #832・仕様書 11 章 U4）。</summary>
/// <remarks>**インスタンスごとの数。** 複数インスタンスでは合計が上限をインスタンス数倍まで超え得る。</remarks>
public sealed class LookupConcurrencyGate
{
    private readonly Dictionary<Guid, int> running = [];
    private readonly Lock gate = new();

    /// <summary>枠を取る。満了なら <c>null</c>。返したものは必ず破棄する。</summary>
    public IDisposable? TryEnter(Guid surveyId, int limit)
    {
        lock (gate)
        {
            running.TryGetValue(surveyId, out var current);
            if (current >= limit)
            {
                return null;
            }

            running[surveyId] = current + 1;
        }

        return new Releaser(this, surveyId);
    }

    /// <summary>試験用。実行中の数。</summary>
    public int RunningOf(Guid surveyId)
    {
        lock (gate)
        {
            return running.GetValueOrDefault(surveyId);
        }
    }

    private void Leave(Guid surveyId)
    {
        lock (gate)
        {
            if (!running.TryGetValue(surveyId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                running.Remove(surveyId);
            }
            else
            {
                running[surveyId] = current - 1;
            }
        }
    }

    private sealed class Releaser(LookupConcurrencyGate owner, Guid surveyId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Leave(surveyId);
            }
        }
    }
}
