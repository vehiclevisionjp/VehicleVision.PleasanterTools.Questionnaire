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
    /// <summary>1 画面で複数の要求が出るものかを見分ける。</summary>
    /// <remarks>
    /// <para>
    /// **本文画像も、組み上げたビルド成果物も、1 画面で何本も要求される。**
    /// 通常 API と同じ枠で数えると、**同じ NAT 配下の利用者が数人開いただけで
    /// アンケート本体まで止まる。**
    /// </para>
    /// <para>
    /// ⚠️ **書体は文字の範囲ごとに分割されている**（<c>wwwroot/assets</c> に
    /// woff2 が 366 個）。**表示する文字が増えるほど追加で読まれる**ので、
    /// 1 画面あたりの本数は content 次第で増える（Issue #316）。
    /// </para>
    /// <para>
    /// ⚠️ **レート制限の外へは出さない。** 上限が無くなるのは別の話。
    /// </para>
    /// </remarks>
    public static bool IsBulkAsset(PathString path)
    {
        // ビルド成果物。**ハッシュ付きの不変な名前**で、性質は本文画像と同じ
        if (path.StartsWithSegments("/assets"))
        {
            return true;
        }

        // 回答画面の本文画像（/api/forms/{publicId}/assets/{assetId}）
        return path.StartsWithSegments("/api/forms")
            && path.Value?.Contains("/assets/", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>アンケート 1 本あたりの枠を作る。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **<c>publicId</c> を持たない要求は、この段で数えない。**
    /// 以前は <c>"none"</c> という 1 つの枠へまとめていたため、
    /// **管理画面・監視 API・資産の配信の合計**が 1 分あたり
    /// <c>permitLimit</c> 件で頭打ちになっていた（Issue #311）。
    /// </para>
    /// <para>
    /// **送信元ごとではなく全体の合計**だったので、
    /// 管理者の操作が増えただけで無関係な利用者が 429 を受け得た。
    /// **実際に、写しの一式が 13 秒で超えて 11 本が落ちた。**
    /// </para>
    /// <para>
    /// **回答画面以外は 1 段目（送信元ごとの枠）で守る。**
    /// この段の役目は「特定のアンケートだけを狙って叩かれること」を防ぐこと。
    /// </para>
    /// </remarks>
    public static RateLimitPartition<string> Survey(
        string? publicId,
        int permitLimit,
        TimeSpan window,
        ISharedRateLimitStore? sharedStore,
        ILogger logger)
    {
        if (publicId is null)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        return FixedWindow(publicId, "survey", permitLimit, window, sharedStore, logger);
    }

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
