using System.Collections.Concurrent;
using StackExchange.Redis;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 再認証（Issue #1034）の途中の状態と、発行 1 回分の許可を預ける<b>使い捨ての置き場</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>取り出す操作（<see cref="TakeAsync"/>）は、読むのと消すのが 1 つの原子操作。</b>
/// 同じ値を 2 人が取り出せない（リプレイ・同時の発行要求で 1 回分を 2 回使えない）ことを、この型が保証する。
/// </para>
/// <para>
/// <b>期限は置くときに必ず付ける。</b>期限を過ぎた値は、取り出せない。
/// </para>
/// <para>
/// 複数のプロセスで動かすときは、共有状態の Redis（<c>QUESTIONNAIRE_SHARED_STATE_STORE=Redis</c>）を使うこと。
/// プロセス内の置き場では、IdP からの戻りが別のプロセスへ届くと状態が見つからず、<b>発行は断られる</b>
/// （通してしまう方向には倒れない。やり直せば通る）。
/// </para>
/// </remarks>
public interface IAdminReauthStateStore
{
    /// <summary>値を置く。同じキーがあれば置き換える。</summary>
    Task PutAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>読んで消す（原子的）。期限切れ・無ければ <c>null</c>。</summary>
    Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>プロセス内の置き場（既定）。</summary>
public sealed class MemoryAdminReauthStateStore(TimeProvider timeProvider) : IAdminReauthStateStore
{
    /// <summary>これ以上は置かない（置き場を膨らませる攻撃への歯止め）。</summary>
    private const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> entries = new(StringComparer.Ordinal);

    public Task PutAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var now = timeProvider.GetUtcNow();
        if (entries.Count >= MaxEntries)
        {
            foreach (var pair in entries)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    entries.TryRemove(pair.Key, out _);
                }
            }

            if (entries.Count >= MaxEntries)
            {
                throw new InvalidOperationException("再認証の状態の置き場がいっぱいです。");
            }
        }

        entries[key] = (value, now + lifetime);
        return Task.CompletedTask;
    }

    public Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        // **TryRemove が原子的。** 2 人が同時に呼んでも、値を受け取れるのは 1 人だけ
        if (entries.TryRemove(key, out var entry) && entry.ExpiresAt > timeProvider.GetUtcNow())
        {
            return Task.FromResult<string?>(entry.Value);
        }

        return Task.FromResult<string?>(null);
    }
}

/// <summary>Redis（Valkey）の置き場。複数のプロセスで共有する。</summary>
public sealed class RedisAdminReauthStateStore(
    IConnectionMultiplexer connection,
    SharedStateOptions options) : IAdminReauthStateStore
{
    /// <summary>GET と DEL を 1 つの Lua で行う（GETDEL は古い Redis に無い）。</summary>
    private const string TakeScript =
        "local value = redis.call('GET', KEYS[1]); "
        + "if value then redis.call('DEL', KEYS[1]); end; "
        + "return value;";

    private RedisKey KeyOf(string key) => $"{options.KeyPrefix}reauth:{key}";

    public async Task PutAsync(string key, string value, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        await connection.GetDatabase().StringSetAsync(KeyOf(key), value, lifetime).ConfigureAwait(false);
    }

    public async Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await connection.GetDatabase()
            .ScriptEvaluateAsync(TakeScript, [KeyOf(key)]).ConfigureAwait(false);
        return result.IsNull ? null : (string?)result;
    }
}
