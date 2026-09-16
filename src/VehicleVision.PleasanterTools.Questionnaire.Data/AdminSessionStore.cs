using System.Data.Common;
using System.Data;
using System.Text.Json;
using Dapper;
using StackExchange.Redis;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理者の認証がどの段階にあるか。</summary>
public enum AdminSessionKind
{
    /// <summary>2 要素まで通った状態。</summary>
    Session = 0,

    /// <summary>パスワードだけ通った状態。</summary>
    Pending = 1,

    /// <summary>2 要素を登録し直している状態。</summary>
    Reenroll = 2,
}

/// <summary>サーバ側に置く管理者セッション。</summary>
public sealed record AdminSessionEntry
{
    public required Guid AdminSessionId { get; init; }
    public required Guid AdminUserId { get; init; }
    public required AdminSessionKind Kind { get; init; }
    public required string ProtectedPayload { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

/// <summary>管理者セッションの入れ物。</summary>
public interface IAdminSessionStore
{
    Task CreateAsync(AdminSessionEntry entry, CancellationToken cancellationToken = default);

    Task<AdminSessionEntry?> FindAsync(
        Guid adminSessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminSessionEntry>> ListAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid adminSessionId, CancellationToken cancellationToken = default);

    /// <summary>指定した通常セッションだけを残し、同じ利用者のほかの状態をすべて失効させる。</summary>
    Task<IReadOnlyList<Guid>> DeleteAllExceptAsync(
        Guid adminUserId,
        Guid? exceptSessionId,
        CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>RDBMS を使う管理者セッションストア。</summary>
public sealed class DatabaseAdminSessionStore(IDbConnectionFactory connectionFactory)
    : IAdminSessionStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task CreateAsync(
        AdminSessionEntry entry,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AdminSessions] "
            + "([AdminSessionId], [AdminUserId], [Kind], [ProtectedPayload], [CreatedAt], "
            + "[ExpiresAt], [IpAddress], [UserAgent]) "
            + "VALUES (@AdminSessionId, @AdminUserId, @Kind, @ProtectedPayload, @CreatedAt, "
            + "@ExpiresAt, @IpAddress, @UserAgent)",
            Parameters(entry),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AdminSessionEntry?> FindAsync(
        Guid adminSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminSessionEntry>(Sql(
            "SELECT [AdminSessionId], [AdminUserId], [Kind], [ProtectedPayload], "
            + "[CreatedAt], [ExpiresAt], [IpAddress], [UserAgent] "
            + "FROM [AdminSessions] WHERE [AdminSessionId] = @AdminSessionId",
            new { AdminSessionId = adminSessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminSessionEntry>> ListAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AdminSessionEntry>(Sql(
            "SELECT [AdminSessionId], [AdminUserId], [Kind], [ProtectedPayload], "
            + "[CreatedAt], [ExpiresAt], [IpAddress], [UserAgent] "
            + "FROM [AdminSessions] "
            + "WHERE [AdminUserId] = @AdminUserId AND [Kind] = @Kind "
            + "ORDER BY [CreatedAt] DESC",
            new { AdminUserId = adminUserId, Kind = (int)AdminSessionKind.Session },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<bool> DeleteAsync(
        Guid adminSessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteAsync(Sql(
            "DELETE FROM [AdminSessions] WHERE [AdminSessionId] = @AdminSessionId",
            new { AdminSessionId = adminSessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count == 1;
    }

    public async Task<IReadOnlyList<Guid>> DeleteAllExceptAsync(
        Guid adminUserId,
        Guid? exceptSessionId,
        CancellationToken cancellationToken = default)
    {
        var parameters = new
        {
            AdminUserId = adminUserId,
            ExceptSessionId = exceptSessionId,
            SessionKind = (int)AdminSessionKind.Session,
        };
        const string condition =
            "[AdminUserId] = @AdminUserId "
            + "AND (@ExceptSessionId IS NULL OR [AdminSessionId] <> @ExceptSessionId "
            + "OR [Kind] <> @SessionKind)";

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            SqlDialect.Format(Provider, $"SELECT [AdminSessionId] FROM [AdminSessions] WHERE {condition}"),
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        await connection.ExecuteAsync(new CommandDefinition(
            SqlDialect.Format(Provider, $"DELETE FROM [AdminSessions] WHERE {condition}"),
            parameters,
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public async Task<int> DeleteExpiredAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AdminSessions] WHERE [ExpiresAt] <= @Now",
            new { Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object Parameters(AdminSessionEntry entry) => new
    {
        entry.AdminSessionId,
        entry.AdminUserId,
        Kind = (int)entry.Kind,
        entry.ProtectedPayload,
        CreatedAt = DbTime.ForDb(entry.CreatedAt),
        ExpiresAt = DbTime.ForDb(entry.ExpiresAt),
        entry.IpAddress,
        entry.UserAgent,
    };

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(
            SqlDialect.Format(Provider, sql),
            parameters,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

/// <summary>Redis を使う管理者セッションストア。</summary>
public sealed class RedisAdminSessionStore(
    IConnectionMultiplexer connection,
    string keyPrefix = "questionnaire:admin-session:") : IAdminSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IDatabase Database => connection.GetDatabase();

    public async Task CreateAsync(
        AdminSessionEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lifetime = entry.ExpiresAt - entry.CreatedAt;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), "期限を過ぎたセッションは作成できない。");
        }

        const string createScript =
            "redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2]); "
            + "redis.call('SADD', KEYS[2], ARGV[3]); "
            + "local ttl = redis.call('PTTL', KEYS[2]); "
            + "if ttl < tonumber(ARGV[2]) then redis.call('PEXPIRE', KEYS[2], ARGV[2]); end; "
            + "return 1;";

        // **本体だけ作って索引に載らない状態を作らない。**
        // 索引に無ければ、パスワード変更時の一括失効から漏れるため。
        await Database.ScriptEvaluateAsync(
            createScript,
            [SessionKey(entry.AdminSessionId), UserKey(entry.AdminUserId)],
            [
                JsonSerializer.Serialize(entry, JsonOptions),
                (long)lifetime.TotalMilliseconds,
                entry.AdminSessionId.ToString("N"),
            ]).ConfigureAwait(false);
    }

    public async Task<AdminSessionEntry?> FindAsync(
        Guid adminSessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await Database.StringGetAsync(SessionKey(adminSessionId)).ConfigureAwait(false);
        return value.IsNull
            ? null
            : JsonSerializer.Deserialize<AdminSessionEntry>((string)value!, JsonOptions);
    }

    public async Task<IReadOnlyList<AdminSessionEntry>> ListAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = await Database.SetMembersAsync(UserKey(adminUserId)).ConfigureAwait(false);
        var entries = new List<AdminSessionEntry>();
        foreach (var value in ids)
        {
            if (!Guid.TryParse(value.ToString(), out var id))
            {
                await Database.SetRemoveAsync(UserKey(adminUserId), value).ConfigureAwait(false);
                continue;
            }

            var entry = await FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                await Database.SetRemoveAsync(UserKey(adminUserId), value).ConfigureAwait(false);
            }
            else if (entry.Kind is AdminSessionKind.Session)
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(entry => entry.CreatedAt).ToList();
    }

    public async Task<bool> DeleteAsync(
        Guid adminSessionId,
        CancellationToken cancellationToken = default)
    {
        var entry = await FindAsync(adminSessionId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return false;
        }

        var removed = await Database.KeyDeleteAsync(SessionKey(adminSessionId)).ConfigureAwait(false);
        await Database.SetRemoveAsync(UserKey(entry.AdminUserId), adminSessionId.ToString("N"))
            .ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<Guid>> DeleteAllExceptAsync(
        Guid adminUserId,
        Guid? exceptSessionId,
        CancellationToken cancellationToken = default)
    {
        var ids = await Database.SetMembersAsync(UserKey(adminUserId)).ConfigureAwait(false);
        var deleted = new List<Guid>();
        foreach (var value in ids)
        {
            if (!Guid.TryParse(value.ToString(), out var id))
            {
                await Database.SetRemoveAsync(UserKey(adminUserId), value).ConfigureAwait(false);
                continue;
            }

            var entry = await FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (entry is not null
                && exceptSessionId == id
                && entry.Kind is AdminSessionKind.Session)
            {
                continue;
            }

            if (await Database.KeyDeleteAsync(SessionKey(id)).ConfigureAwait(false))
            {
                deleted.Add(id);
            }

            await Database.SetRemoveAsync(UserKey(adminUserId), value).ConfigureAwait(false);
        }

        return deleted;
    }

    public Task<int> DeleteExpiredAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Redis は各セッションの TTL で期限切れを削除する。
        return Task.FromResult(0);
    }

    private RedisKey SessionKey(Guid id) => $"{keyPrefix}entry:{id:N}";

    private RedisKey UserKey(Guid id) => $"{keyPrefix}user:{id:N}";
}
