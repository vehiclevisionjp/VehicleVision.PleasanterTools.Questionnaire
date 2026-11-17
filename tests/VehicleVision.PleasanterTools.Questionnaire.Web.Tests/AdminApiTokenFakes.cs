using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// 記憶の上だけで動く <see cref="IAdminApiTokenStore"/>。
/// </summary>
/// <remarks>
/// <b>DB を立てずに、呼び出し側の決まり（期限・スコープ・失効・自動失効・平文を残さない）だけを確かめるため。</b>
/// 本物の SQL が SQLite で動くことは、結合試験（<c>AdminApiTokenStoreTests</c>・<c>AdminApiTokenEndToEndTests</c>）の受け持ち。
/// <b>本物と同じ条件</b>（持ち主の組で絞る・すでに失効したものは書き換えない・最終使用の間引き）を書く。
/// </remarks>
internal sealed class FakeAdminApiTokenStore : IAdminApiTokenStore
{
    private sealed class Entry
    {
        public required NewAdminApiToken Source { get; init; }

        public DateTime? RevokedAt { get; set; }

        public Guid? RevokedBy { get; set; }

        public string? RevokeReason { get; set; }

        public DateTime? LastUsedAt { get; set; }

        public string? LastUsedIp { get; set; }

        public AdminApiTokenRecord ToRecord() => new(
            Source.TokenId,
            Source.OwnerAdminUserId,
            Source.Name,
            Source.Prefix,
            Source.Scopes,
            Source.IpAllowList,
            Source.CreatedAt,
            Source.ExpiresAt,
            RevokedAt,
            RevokedBy,
            RevokeReason,
            LastUsedAt,
            LastUsedIp);
    }

    private readonly List<Entry> entries = [];

    /// <summary>保存されている値を、すべて 1 つの文字列にしたもの。<b>平文が入っていないことの確認に使う。</b></summary>
    public string DumpAll() =>
        string.Join('\n', entries.Select(entry => string.Join(
            '|',
            entry.Source.TokenId,
            entry.Source.OwnerAdminUserId,
            entry.Source.Name,
            entry.Source.Prefix,
            entry.Source.TokenHash,
            string.Join(' ', entry.Source.Scopes),
            string.Join(',', entry.Source.IpAllowList),
            entry.RevokeReason,
            entry.LastUsedIp)));

    /// <summary>保存されているハッシュ。</summary>
    public IReadOnlyList<string> StoredHashes => entries.Select(entry => entry.Source.TokenHash).ToList();

    /// <summary>最後に使った日時を書いた回数。</summary>
    public int TouchWrites { get; private set; }

    public Task CreateAsync(NewAdminApiToken token, CancellationToken cancellationToken = default)
    {
        // **ハッシュは一意。** 本物は一意索引で守る
        if (entries.Any(entry => entry.Source.TokenHash == token.TokenHash))
        {
            throw new InvalidOperationException("同じハッシュのトークンがある");
        }

        entries.Add(new Entry { Source = token });
        return Task.CompletedTask;
    }

    public Task<AdminApiTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.FirstOrDefault(entry => entry.Source.TokenHash == tokenHash)?.ToRecord());

    public Task<AdminApiTokenRecord?> FindAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.FirstOrDefault(entry => entry.Source.TokenId == tokenId)?.ToRecord());

    public Task<IReadOnlyList<AdminApiTokenRecord>> ListByOwnerAsync(
        Guid ownerAdminUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdminApiTokenRecord>>(entries
            .Where(entry => entry.Source.OwnerAdminUserId == ownerAdminUserId)
            .OrderByDescending(entry => entry.Source.CreatedAt)
            .Select(entry => entry.ToRecord())
            .ToList());

    public Task<int> CountActiveAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(entries.Count(entry =>
            entry.Source.OwnerAdminUserId == ownerAdminUserId
            && entry.RevokedAt is null
            && entry.Source.ExpiresAt > nowUtc));

    public Task<bool> RevokeAsync(
        Guid tokenId,
        Guid? ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var entry = entries.FirstOrDefault(candidate =>
            candidate.Source.TokenId == tokenId
            && candidate.RevokedAt is null
            && (ownerAdminUserId is null || candidate.Source.OwnerAdminUserId == ownerAdminUserId));
        if (entry is null)
        {
            return Task.FromResult(false);
        }

        entry.RevokedAt = nowUtc;
        entry.RevokedBy = revokedBy;
        entry.RevokeReason = reason;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<AdminApiTokenRecord>> RevokeAllForOwnerAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var targets = entries
            .Where(entry => entry.Source.OwnerAdminUserId == ownerAdminUserId
                && entry.RevokedAt is null
                && entry.Source.ExpiresAt > nowUtc)
            .ToList();
        foreach (var entry in targets)
        {
            entry.RevokedAt = nowUtc;
            entry.RevokedBy = revokedBy;
            entry.RevokeReason = reason;
        }

        return Task.FromResult<IReadOnlyList<AdminApiTokenRecord>>(targets.Select(entry => entry.ToRecord()).ToList());
    }

    public Task<bool> TouchLastUsedAsync(
        Guid tokenId,
        DateTime nowUtc,
        string? ipAddress,
        TimeSpan minInterval,
        CancellationToken cancellationToken = default)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Source.TokenId == tokenId);
        if (entry is null || (entry.LastUsedAt is { } last && last > nowUtc - minInterval))
        {
            return Task.FromResult(false);
        }

        entry.LastUsedAt = nowUtc;
        entry.LastUsedIp = ipAddress;
        TouchWrites++;
        return Task.FromResult(true);
    }
}
