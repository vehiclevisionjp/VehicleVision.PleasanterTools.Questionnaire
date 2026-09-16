using System.Threading.RateLimiting;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Redis 障害時にもプロセス内の固定窓へ倒せる共有レート制限。</summary>
public sealed class SharedFixedWindowRateLimiter(
    ISharedRateLimitStore store,
    string partition,
    int permitLimit,
    TimeSpan window,
    ILogger logger) : RateLimiter
{
    private static readonly RateLimitLease AsyncOnlyLease =
        new SharedRateLimitLease(TimeSpan.Zero);

    private readonly FixedWindowRateLimiter _local = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true,
    });
    private readonly SharedStateWarning _warning = new(logger);

    public override TimeSpan? IdleDuration => _local.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _local.GetStatistics();

    // Redis は非同期 API しか持たないため、同期取得を成功させず必ず共有側を通す。
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => AsyncOnlyLease;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        // Redis が落ちた瞬間に枠が空へ戻らないよう、共有側が正常でもローカル枠を進めておく。
        // 共有側が拒否した要求も数えるため、障害直後は厳しい側へ倒れる。
        var localLease = _local.AttemptAcquire(permitCount);
        try
        {
            var result = await store
                .AcquireAsync(partition, permitCount, permitLimit, window, cancellationToken)
                .ConfigureAwait(false);
            localLease.Dispose();
            return result.Acquired
                ? SharedRateLimitLease.Acquired
                : new SharedRateLimitLease(result.RetryAfter);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _warning.Log(
                exception,
                "共有レート制限の KVS に接続できないため、プロセス内の制限へ切り替えた");
            return localLease;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _local.Dispose();
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _local.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class SharedRateLimitLease(TimeSpan? retryAfter = null) : RateLimitLease
    {
        public static readonly SharedRateLimitLease Acquired = new();

        public override bool IsAcquired => retryAfter is null;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

/// <summary>設定に応じてプロセス内または共有の固定窓を作る。</summary>
public static class RateLimitPartitions
{
    public static RateLimitPartition<string> FixedWindow(
        string partition,
        string scope,
        int permitLimit,
        TimeSpan window,
        ISharedRateLimitStore? sharedStore,
        ILogger logger)
    {
        if (sharedStore is null)
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partition,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                });
        }

        return new RateLimitPartition<string>(
            partition,
            _ => new SharedFixedWindowRateLimiter(
                sharedStore,
                scope + ":" + partition,
                permitLimit,
                window,
                logger));
    }
}
