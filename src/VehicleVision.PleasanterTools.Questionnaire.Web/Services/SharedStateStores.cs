using System.Collections.Immutable;
using System.Text.Json;
using StackExchange.Redis;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>複数プロセスで共有する状態の設定。</summary>
public sealed record SharedStateOptions
{
    public const string StoreKey = "QUESTIONNAIRE_SHARED_STATE_STORE";
    public const string ConnectionStringKey = "QUESTIONNAIRE_ADMIN_SESSION_REDIS_CONNECTIONSTRING";
    public const string PrefixKey = "QUESTIONNAIRE_SHARED_STATE_REDIS_PREFIX";

    /// <summary>Redis を使うか。未設定時はプロセス内のままにする。</summary>
    public bool UseRedis { get; init; }

    /// <summary>共有状態の Redis キー接頭辞。</summary>
    public string KeyPrefix { get; init; } = "questionnaire:shared-state:";

    public static SharedStateOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var store = configuration[StoreKey];
        if (string.IsNullOrWhiteSpace(store)
            || string.Equals(store, "Process", StringComparison.OrdinalIgnoreCase))
        {
            return new SharedStateOptions();
        }

        if (!string.Equals(store, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{StoreKey} must be Process or Redis (current value: {store}).");
        }

        return new SharedStateOptions
        {
            UseRedis = true,
            KeyPrefix = configuration[PrefixKey] ?? "questionnaire:shared-state:",
        };
    }
}

/// <summary>Redis 障害の警告を要求ごとに繰り返さないための抑止器。</summary>
internal sealed class SharedStateWarning(ILogger logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _nextWarningAt;

    public void Log(Exception exception, string message)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var next = Volatile.Read(ref _nextWarningAt);
        if (now < next
            || Interlocked.CompareExchange(
                ref _nextWarningAt, now + TimeSpan.FromMinutes(1).Ticks, next) != next)
        {
            return;
        }

        logger.LogWarning(exception, "{Message}", message);
    }
}

/// <summary>共有固定窓の加算結果。</summary>
public readonly record struct SharedRateLimitResult(bool Acquired, TimeSpan RetryAfter);

/// <summary>共有固定窓を数える口。</summary>
public interface ISharedRateLimitStore
{
    ValueTask<SharedRateLimitResult> AcquireAsync(
        string partition,
        int permitCount,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}

/// <summary>Redis で固定窓を数える。</summary>
public sealed class RedisSharedRateLimitStore(
    IConnectionMultiplexer connection,
    SharedStateOptions options) : ISharedRateLimitStore
{
    private const string AcquireScript =
        "local count = redis.call('INCRBY', KEYS[1], ARGV[1]); "
        + "if count == tonumber(ARGV[1]) then redis.call('PEXPIRE', KEYS[1], ARGV[3]); end; "
        + "local ttl = redis.call('PTTL', KEYS[1]); "
        + "return {count, ttl};";

    public async ValueTask<SharedRateLimitResult> AcquireAsync(
        string partition,
        int permitCount,
        int permitLimit,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (RedisResult[]?)await connection.GetDatabase()
            .ScriptEvaluateAsync(
                AcquireScript,
                [options.KeyPrefix + "rate:" + partition],
                [permitCount, permitLimit, Math.Max(1L, (long)window.TotalMilliseconds)])
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Redis からレート制限の結果が返らなかった。");
        cancellationToken.ThrowIfCancellationRequested();

        var count = (long)result[0];
        var ttlMilliseconds = Math.Max(0L, (long)result[1]);
        return new SharedRateLimitResult(
            count <= permitLimit,
            TimeSpan.FromMilliseconds(ttlMilliseconds));
    }
}

/// <summary>共有した滞留の状態。</summary>
public sealed record SharedBacklogState(
    int Total,
    ImmutableDictionary<Guid, int> BySurvey,
    bool TotalBlocked,
    ImmutableHashSet<Guid> BlockedSurveys,
    DateTimeOffset? SampledAt)
{
    public int For(Guid surveyId) => BySurvey.GetValueOrDefault(surveyId);
}

/// <summary>滞留の計測値・受付後増分・停止フラグを共有する口。</summary>
public interface ISharedBacklogStateStore
{
    Task<SharedBacklogState> GetOrSampleAsync(
        TimeSpan sampleInterval,
        int perSurveyAtLeast,
        Func<CancellationToken, Task<PendingBacklog>> sample,
        CancellationToken cancellationToken = default);

    Task IncrementAcceptedAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default);

    Task<bool> SetTotalBlockedAsync(
        bool blocked,
        CancellationToken cancellationToken = default);

    Task<bool> SetSurveyBlockedAsync(
        Guid surveyId,
        bool blocked,
        CancellationToken cancellationToken = default);
}

/// <summary>Redis で滞留の状態を共有する。</summary>
public sealed class RedisSharedBacklogStateStore(
    IConnectionMultiplexer connection,
    SharedStateOptions options,
    ILogger<RedisSharedBacklogStateStore> logger,
    TimeProvider? timeProvider = null) : ISharedBacklogStateStore
{
    private const string TotalField = "__total";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IDatabase _database = connection.GetDatabase();
    private readonly string _sampleKey = options.KeyPrefix + "backlog:sample";
    private readonly string _acceptedKey = options.KeyPrefix + "backlog:accepted";
    private readonly string _sampleLockKey = options.KeyPrefix + "backlog:sample-lock";
    private readonly string _totalBlockedKey = options.KeyPrefix + "backlog:blocked-total";
    private readonly string _blockedSurveysKey = options.KeyPrefix + "backlog:blocked-surveys";

    private const string CompleteSampleScript =
        "if redis.call('GET', KEYS[3]) ~= ARGV[1] then return 0 end; "
        + "redis.call('SET', KEYS[1], ARGV[2]); "
        + "for i = 3, #ARGV, 2 do "
        + "local left = redis.call('HINCRBY', KEYS[2], ARGV[i], -tonumber(ARGV[i + 1])); "
        + "if left <= 0 then redis.call('HDEL', KEYS[2], ARGV[i]); end; "
        + "end; "
        + "redis.call('DEL', KEYS[3]); return 1;";

    private const string ReleaseLockScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then "
        + "return redis.call('DEL', KEYS[1]) else return 0 end;";

    private const string SetFlagScript =
        "local current = redis.call('GET', KEYS[1]); "
        + "if (current or '0') == ARGV[1] then return 0 end; "
        + "redis.call('SET', KEYS[1], ARGV[1]); return 1;";

    private const string IncrementAcceptedScript =
        "redis.call('HINCRBY', KEYS[1], ARGV[1], 1); "
        + "redis.call('HINCRBY', KEYS[1], ARGV[2], 1); return 1;";

    public async Task<SharedBacklogState> GetOrSampleAsync(
        TimeSpan sampleInterval,
        int perSurveyAtLeast,
        Func<CancellationToken, Task<PendingBacklog>> sample,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (state.SampledAt is { } sampledAt && now - sampledAt < sampleInterval)
        {
            return state;
        }

        var token = Guid.NewGuid().ToString("N");
        var lockLifetime = TimeSpan.FromSeconds(Math.Max(30d, sampleInterval.TotalSeconds * 3d));
        cancellationToken.ThrowIfCancellationRequested();
        var ownsLock = await _database
            .StringSetAsync(_sampleLockKey, token, lockLifetime, When.NotExists)
            .ConfigureAwait(false);

        if (!ownsLock)
        {
            return state;
        }

        try
        {
            var acceptedBefore = await _database.HashGetAllAsync(_acceptedKey).ConfigureAwait(false);
            PendingBacklog measured;
            try
            {
                measured = await sample(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "滞留の件数を数えられなかった。前回の共有値のまま続ける");
                return state;
            }

            var envelope = new BacklogSample(
                measured.Total,
                measured.BySurvey.ToDictionary(),
                _time.GetUtcNow());
            var arguments = new List<RedisValue>
            {
                token,
                JsonSerializer.Serialize(envelope, JsonOptions),
            };
            foreach (var entry in acceptedBefore)
            {
                arguments.Add(entry.Name);
                arguments.Add(entry.Value);
            }

            await _database.ScriptEvaluateAsync(
                    CompleteSampleScript,
                    [_sampleKey, _acceptedKey, _sampleLockKey],
                    [.. arguments])
                .ConfigureAwait(false);
        }
        finally
        {
            await _database.ScriptEvaluateAsync(
                    ReleaseLockScript,
                    [_sampleLockKey],
                    [token])
                .ConfigureAwait(false);
        }

        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task IncrementAcceptedAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.ScriptEvaluateAsync(
                IncrementAcceptedScript,
                [_acceptedKey],
                [TotalField, surveyId.ToString("N")])
            .ConfigureAwait(false);
    }

    public async Task<bool> SetTotalBlockedAsync(
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = await _database.ScriptEvaluateAsync(
                SetFlagScript,
                [_totalBlockedKey],
                [blocked ? "1" : "0"])
            .ConfigureAwait(false);
        return (long)changed == 1;
    }

    public async Task<bool> SetSurveyBlockedAsync(
        Guid surveyId,
        bool blocked,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = blocked
            ? await _database.SetAddAsync(_blockedSurveysKey, surveyId.ToString("N"))
                .ConfigureAwait(false)
            : await _database.SetRemoveAsync(_blockedSurveysKey, surveyId.ToString("N"))
                .ConfigureAwait(false);
        return changed;
    }

    private async Task<SharedBacklogState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sampleTask = _database.StringGetAsync(_sampleKey);
        var acceptedTask = _database.HashGetAllAsync(_acceptedKey);
        var totalBlockedTask = _database.StringGetAsync(_totalBlockedKey);
        var blockedSurveysTask = _database.SetMembersAsync(_blockedSurveysKey);
        await Task.WhenAll(sampleTask, acceptedTask, totalBlockedTask, blockedSurveysTask)
            .ConfigureAwait(false);

        var envelope = sampleTask.Result.IsNull
            ? null
            : JsonSerializer.Deserialize<BacklogSample>(
                (string)sampleTask.Result!, JsonOptions);
        var total = envelope?.Total ?? 0;
        var bySurvey = envelope?.BySurvey.ToDictionary() ?? [];

        foreach (var entry in acceptedTask.Result)
        {
            var count = (int)(long)entry.Value;
            if (entry.Name == TotalField)
            {
                total += count;
            }
            else if (Guid.TryParse(entry.Name.ToString(), out var surveyId))
            {
                bySurvey[surveyId] = bySurvey.GetValueOrDefault(surveyId) + count;
            }
        }

        return new SharedBacklogState(
            total,
            bySurvey.ToImmutableDictionary(),
            totalBlockedTask.Result == "1",
            blockedSurveysTask.Result
                .Select(value =>
                    Guid.TryParse(value.ToString(), out var surveyId) ? surveyId : Guid.Empty)
                .Where(surveyId => surveyId != Guid.Empty)
                .ToImmutableHashSet(),
            envelope?.SampledAt);
    }

    private sealed record BacklogSample(
        int Total,
        Dictionary<Guid, int> BySurvey,
        DateTimeOffset SampledAt);
}
