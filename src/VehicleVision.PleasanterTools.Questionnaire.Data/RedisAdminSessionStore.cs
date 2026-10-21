using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理者セッションを Redis（Valkey）へ保管する。Core.Data の <see cref="IAdminSessionStore"/> の実装。</summary>
/// <remarks>
/// <para>
/// <b>Core.Data の RDBMS 版（<c>AdminSessionStore</c>）と同じ約束で動く。</b>
/// cookie に載せるのは推測できない 256 bit の ID だけで、<b>Redis のキーには SHA-256（端末を指す識別子）だけ</b>を使う
/// （Redis が漏れても、そこから有効な cookie は作れない）。
/// </para>
/// <para>
/// キーは 3 種類。<c>entry:{識別子}</c>（本体。<b>寿命は Redis の TTL</b>）、
/// <c>user:{利用者}</c>（その利用者の識別子の集合。一覧と一括失効に使う）、
/// <c>saml:{NameID の SHA-256}</c>（SAML の鍵で探すための集合）。
/// 集合も本体の寿命に合わせて期限を付け、本体が先に消えた分は読むときに掃除する。
/// </para>
/// <para>
/// <b>期限切れの掃除（<see cref="DeleteExpiredAsync"/>）は何もしない。</b>各キーの TTL が消す。
/// </para>
/// </remarks>
public sealed class RedisAdminSessionStore(
    IConnectionMultiplexer connection,
    TimeProvider time,
    string keyPrefix = "questionnaire:admin-session:") : IAdminSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IDatabase Database => connection.GetDatabase();

    /// <summary>端末を指す識別子（セッション ID の SHA-256。小文字の 16 進）。Core.Data の RDBMS 版と同じ作り方。</summary>
    private static string IdHash(string sessionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    public string HandleOf(string sessionId) => IdHash(sessionId);

    public async Task<string> CreateAsync(
        Guid userId,
        string kind,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null,
        SessionSso? sso = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "期限を過ぎたセッションは作成できない。");
        }

        var now = time.GetUtcNow();
        var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var handle = IdHash(sessionId);
        var saml = sso?.Saml;
        var pleasanter = sso?.Pleasanter;

        var stored = new StoredSession
        {
            UserId = userId,
            Kind = kind,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            IpAddress = AuditText.Safe(ipAddress, 64),
            UserAgent = AuditText.Safe(userAgent, 512),
            SamlNameId = AuditText.Safe(saml?.NameId, 256),
            SamlSessionIndex = AuditText.Safe(saml?.SessionIndex, 256),
            PleasanterTenantId = pleasanter?.TenantId,
            PleasanterUserId = pleasanter?.UserId,
            PleasanterVerifiedAt = pleasanter?.VerifiedAt,
        };

        // **本体だけ作って索引に載らない状態を作らない。**
        // 索引に無ければ、パスワード変更時の一括失効から漏れるため。
        const string createScript =
            "redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2]); "
            + "redis.call('SADD', KEYS[2], ARGV[3]); "
            + "local ttl = redis.call('PTTL', KEYS[2]); "
            + "if ttl < tonumber(ARGV[2]) then redis.call('PEXPIRE', KEYS[2], ARGV[2]); end; "
            + "if ARGV[4] == '1' then "
            + "redis.call('SADD', KEYS[3], ARGV[3]); "
            + "local samlTtl = redis.call('PTTL', KEYS[3]); "
            + "if samlTtl < tonumber(ARGV[2]) then redis.call('PEXPIRE', KEYS[3], ARGV[2]); end; "
            + "end; "
            + "return 1;";

        await Database.ScriptEvaluateAsync(
            createScript,
            [EntryKey(handle), UserKey(userId), SamlKey(stored.SamlNameId ?? string.Empty)],
            [
                JsonSerializer.Serialize(stored, JsonOptions),
                (long)lifetime.TotalMilliseconds,
                handle,
                string.IsNullOrEmpty(stored.SamlNameId) ? "0" : "1",
            ]).ConfigureAwait(false);

        return sessionId;
    }

    public async Task<AdminSession?> FindAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(IdHash(sessionId), cancellationToken).ConfigureAwait(false);
        return stored is null || stored.ExpiresAt <= time.GetUtcNow()
            ? null
            : new AdminSession(stored.UserId, stored.Kind, stored.ExpiresAt, stored.ToPleasanterKeys());
    }

    public async Task<SessionSso?> GetSsoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var stored = await ReadAsync(IdHash(sessionId), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var saml = stored.ToSamlKeys();
        var pleasanter = stored.ToPleasanterKeys();
        return saml is null && pleasanter is null ? null : new SessionSso(saml, pleasanter);
    }

    public async Task TouchPleasanterVerifiedAsync(
        string sessionId,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        var handle = IdHash(sessionId);
        var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return;
        }

        stored.PleasanterVerifiedAt = verifiedAt;

        // **寿命は変えない**（残りの TTL のまま、既にあるキーだけを書き換える。KEEPTTL は古い Redis に無いので Lua で行う）
        const string touchScript =
            "local ttl = redis.call('PTTL', KEYS[1]); "
            + "if ttl > 0 then redis.call('SET', KEYS[1], ARGV[1], 'PX', ttl); return 1; end; "
            + "return 0;";
        await Database.ScriptEvaluateAsync(
            touchScript,
            [EntryKey(handle)],
            [JsonSerializer.Serialize(stored, JsonOptions)]).ConfigureAwait(false);
    }

    public async Task<SamlSessionKeys?> GetSamlKeysAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        (await ReadAsync(IdHash(sessionId), cancellationToken).ConfigureAwait(false))?.ToSamlKeys();

    public async Task<int> DeleteBySamlKeysAsync(
        string nameId,
        string? sessionIndex,
        CancellationToken cancellationToken = default)
    {
        var members = await Database.SetMembersAsync(SamlKey(nameId)).ConfigureAwait(false);
        var deleted = 0;
        foreach (var member in members)
        {
            var handle = member.ToString();
            var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                await Database.SetRemoveAsync(SamlKey(nameId), member).ConfigureAwait(false);
                continue;
            }

            if (!string.IsNullOrEmpty(sessionIndex)
                && !string.Equals(stored.SamlSessionIndex, sessionIndex, StringComparison.Ordinal))
            {
                continue;
            }

            if (await RemoveAsync(handle, stored).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public async Task<IReadOnlyList<AdminSessionInfo>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        var members = await Database.SetMembersAsync(UserKey(userId)).ConfigureAwait(false);
        var rows = new List<(string Handle, StoredSession Stored)>();
        foreach (var member in members)
        {
            var handle = member.ToString();
            var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                // **本体が先に（TTL で）消えた分を掃除する**
                await Database.SetRemoveAsync(UserKey(userId), member).ConfigureAwait(false);
            }
            else if (stored.Kind == AdminSessionKinds.Session && stored.ExpiresAt > now)
            {
                rows.Add((handle, stored));
            }
        }

        return
        [
            .. rows
                .OrderByDescending(row => row.Stored.CreatedAt)
                .ThenByDescending(row => row.Handle, StringComparer.Ordinal)
                .Select(row => new AdminSessionInfo(
                    row.Handle,
                    row.Stored.CreatedAt,
                    row.Stored.ExpiresAt,
                    row.Stored.IpAddress,
                    row.Stored.UserAgent,
                    row.Stored.Via)),
        ];
    }

    public async Task<bool> DeleteByHandleAsync(
        Guid userId,
        string handle,
        CancellationToken cancellationToken = default)
    {
        // **持ち主の組で消す**（他人の端末の識別子を渡されても消えない）
        var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
        return stored is not null
            && stored.UserId == userId
            && await RemoveAsync(handle, stored).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var handle = IdHash(sessionId);
        var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
        if (stored is not null)
        {
            await RemoveAsync(handle, stored).ConfigureAwait(false);
        }
    }

    public async Task DeleteForUserAsync(
        Guid userId,
        string? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var except = exceptSessionId is null ? null : IdHash(exceptSessionId);
        var members = await Database.SetMembersAsync(UserKey(userId)).ConfigureAwait(false);
        foreach (var member in members)
        {
            var handle = member.ToString();
            if (except is not null && string.Equals(handle, except, StringComparison.Ordinal))
            {
                continue;
            }

            var stored = await ReadAsync(handle, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                await Database.SetRemoveAsync(UserKey(userId), member).ConfigureAwait(false);
            }
            else
            {
                await RemoveAsync(handle, stored).ConfigureAwait(false);
            }
        }
    }

    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Redis は各セッションの TTL で期限切れを削除する。
        return Task.FromResult(0);
    }

    private async Task<StoredSession?> ReadAsync(string handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await Database.StringGetAsync(EntryKey(handle)).ConfigureAwait(false);
        return value.IsNull ? null : JsonSerializer.Deserialize<StoredSession>((string)value!, JsonOptions);
    }

    /// <summary>本体と、利用者・SAML の集合の両方から外す。</summary>
    private async Task<bool> RemoveAsync(string handle, StoredSession stored)
    {
        var removed = await Database.KeyDeleteAsync(EntryKey(handle)).ConfigureAwait(false);
        await Database.SetRemoveAsync(UserKey(stored.UserId), handle).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored.SamlNameId))
        {
            await Database.SetRemoveAsync(SamlKey(stored.SamlNameId), handle).ConfigureAwait(false);
        }

        return removed;
    }

    private RedisKey EntryKey(string handle) => $"{keyPrefix}entry:{handle}";

    private RedisKey UserKey(Guid userId) => $"{keyPrefix}user:{userId:N}";

    /// <summary>NameID は長さも文字も決まっていないので、ハッシュにしてキーへ使う。</summary>
    private RedisKey SamlKey(string nameId) => $"{keyPrefix}saml:{IdHash(nameId)}";

    /// <summary>Redis へ置く形。<b>ID の現物は持たない</b>（識別子はキーの名前）。</summary>
    private sealed class StoredSession
    {
        public Guid UserId { get; set; }

        public string Kind { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }

        public string? SamlNameId { get; set; }

        public string? SamlSessionIndex { get; set; }

        public int? PleasanterTenantId { get; set; }

        public int? PleasanterUserId { get; set; }

        public DateTimeOffset? PleasanterVerifiedAt { get; set; }

        /// <summary>入った方式（表示用）。Core.Data の RDBMS 版と同じ判断（SAML → Pleasanter → パスワード）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Via =>
            !string.IsNullOrEmpty(SamlNameId) ? SessionVia.Saml
            : PleasanterUserId is not null ? SessionVia.Pleasanter
            : SessionVia.Password;

        public SamlSessionKeys? ToSamlKeys() =>
            SamlNameId is { Length: > 0 } nameId ? new SamlSessionKeys(nameId, SamlSessionIndex) : null;

        public PleasanterSsoKeys? ToPleasanterKeys() =>
            PleasanterTenantId is { } tenantId && PleasanterUserId is { } userId && PleasanterVerifiedAt is { } verifiedAt
                ? new PleasanterSsoKeys(tenantId, userId, verifiedAt)
                : null;
    }
}
