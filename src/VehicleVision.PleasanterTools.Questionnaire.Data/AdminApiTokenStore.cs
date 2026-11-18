using System.Data.Common;
using System.Security.Cryptography;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理 API の個人トークン 1 本分。<b>ハッシュも平文も含めない</b>（一覧・監査へ出してよい形）。</summary>
/// <param name="TokenId">トークンの識別子（<b>秘密ではない</b>）。</param>
/// <param name="OwnerAdminUserId">持ち主（発行した管理者本人）。</param>
/// <param name="Name">利用者が付けた名前。</param>
/// <param name="Prefix">識別用の接頭辞（<c>qat_</c> ＋ 8 文字）。</param>
/// <param name="Scopes">付けたスコープ（権限名）。</param>
/// <param name="IpAllowList">接続元の許可リスト（CIDR）。空なら制限しない。</param>
/// <param name="CreatedAt">発行日時（UTC）。</param>
/// <param name="ExpiresAt">期限（UTC。必須）。</param>
/// <param name="RevokedAt">失効日時（UTC）。失効していなければ <c>null</c>。</param>
/// <param name="RevokedBy">失効させた管理者。持ち主の停止などの自動失効では <c>null</c>。</param>
/// <param name="RevokeReason">失効の理由の符号（<see cref="AdminApiTokenRevokeReasons"/>）。</param>
/// <param name="LastUsedAt">最後に使った日時（UTC。間引いて更新する）。</param>
/// <param name="LastUsedIp">最後に使った接続元。</param>
public sealed record AdminApiTokenRecord(
    Guid TokenId,
    Guid OwnerAdminUserId,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> IpAllowList,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    Guid? RevokedBy,
    string? RevokeReason,
    DateTime? LastUsedAt,
    string? LastUsedIp)
{
    /// <summary>その時点で使える状態か（失効しておらず、期限内）。持ち主の状態は含まない。</summary>
    public bool IsActiveAt(DateTime nowUtc) => RevokedAt is null && ExpiresAt > nowUtc;
}

/// <summary>失効の理由の符号。<b>DB へ文字列で入る。名前を変えないこと。</b></summary>
public static class AdminApiTokenRevokeReasons
{
    /// <summary>持ち主本人が失効させた。</summary>
    public const string Owner = "owner";

    /// <summary>ほかの管理者（<c>users.write</c> を持つ人）が失効させた。</summary>
    public const string Administrator = "administrator";

    /// <summary>持ち主が止められた（自動）。</summary>
    public const string OwnerDisabled = "ownerDisabled";

    /// <summary>持ち主のパスワードの変更に合わせて失効させた。</summary>
    public const string PasswordChanged = "passwordChanged";

    /// <summary>持ち主の 2 要素の解除に合わせて失効させた。</summary>
    public const string TwoFactorReset = "twoFactorReset";
}

/// <summary>新しく作るトークン。<b>ハッシュだけを渡す。平文はここへ来ない。</b></summary>
public sealed record NewAdminApiToken(
    Guid TokenId,
    Guid OwnerAdminUserId,
    string Name,
    string Prefix,
    string TokenHash,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> IpAllowList,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>管理 API の個人トークンの読み書き（Issue #1025）。</summary>
/// <remarks>
/// <para>
/// ⚠️ <b>扱うのはハッシュだけ。</b>元のトークンはここへ渡さない（渡す側がハッシュにしてから呼ぶ。
/// <c>Web/Services/AdminApiTokenSecret</c>）。ハッシュを返す口は <see cref="FindByHashAsync"/> の引数だけで、
/// 読み出した行（<see cref="AdminApiTokenRecord"/>）にハッシュは載らない。
/// </para>
/// </remarks>
public interface IAdminApiTokenStore
{
    /// <summary>1 本作る。</summary>
    Task CreateAsync(NewAdminApiToken token, CancellationToken cancellationToken = default);

    /// <summary>ハッシュで引く。<b>失効・期限切れも返す</b>（呼ぶ側が状態を判定する）。無ければ <c>null</c>。</summary>
    Task<AdminApiTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>識別子で引く。</summary>
    Task<AdminApiTokenRecord?> FindAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>持ち主のトークンを、新しい順に返す（失効・期限切れを含む）。</summary>
    Task<IReadOnlyList<AdminApiTokenRecord>> ListByOwnerAsync(
        Guid ownerAdminUserId,
        CancellationToken cancellationToken = default);

    /// <summary>持ち主の、いま使えるトークンの本数（失効しておらず、期限内）。</summary>
    Task<int> CountActiveAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>1 本失効させる。<b>すでに失効していれば何もしない</b>。</summary>
    /// <param name="ownerAdminUserId">
    /// 指定すると、<b>その人のトークンだけ</b>を対象にする（他人の識別子を渡されても失効しない）。
    /// </param>
    /// <returns>この呼び出しで失効させたか。</returns>
    Task<bool> RevokeAsync(
        Guid tokenId,
        Guid? ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>持ち主の、いま使えるトークンをすべて失効させる。</summary>
    /// <returns>失効させたトークン（接頭辞つき。監査に残すため）。</returns>
    Task<IReadOnlyList<AdminApiTokenRecord>> RevokeAllForOwnerAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>最後に使った日時を更新する。<b>間引く</b>（前回から <paramref name="minInterval"/> 以上空いたときだけ書く）。</summary>
    /// <returns>書いたか。</returns>
    Task<bool> TouchLastUsedAsync(
        Guid tokenId,
        DateTime nowUtc,
        string? ipAddress,
        TimeSpan minInterval,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。表は <c>AdminApiTokens</c>（M0042）。</summary>
public sealed class AdminApiTokenStore(IDbConnectionFactory connectionFactory) : IAdminApiTokenStore
{
    private const string Columns =
        "[TokenId], [OwnerAdminUserId], [Name], [Prefix], [Scopes], [IpAllowList], [CreatedAt], [ExpiresAt], "
        + "[RevokedAt], [RevokedBy], [RevokeReason], [LastUsedAt], [LastUsedIp]";

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task CreateAsync(NewAdminApiToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(token.TokenHash);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AdminApiTokens] "
            + "([TokenId], [OwnerAdminUserId], [Name], [Prefix], [TokenHash], [Scopes], [IpAllowList], [CreatedAt], [ExpiresAt]) "
            + "VALUES (@TokenId, @OwnerAdminUserId, @Name, @Prefix, @TokenHash, @Scopes, @IpAllowList, @CreatedAt, @ExpiresAt)",
            new
            {
                token.TokenId,
                token.OwnerAdminUserId,
                token.Name,
                token.Prefix,
                token.TokenHash,
                Scopes = string.Join(' ', token.Scopes),
                IpAllowList = token.IpAllowList.Count == 0 ? null : string.Join(',', token.IpAllowList),
                CreatedAt = DbTime.ForDb(token.CreatedAt),
                ExpiresAt = DbTime.ForDb(token.ExpiresAt),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AdminApiTokenRecord?> FindByHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            $"SELECT {Columns}, [TokenHash] FROM [AdminApiTokens] WHERE [TokenHash] = @TokenHash",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // **引いたうえで、定数時間で比べ直す。** 大文字小文字を区別しない照合順序（SQL Server の既定）でも、
        // 別の値が当たらないようにする
        return row is not null
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(row.TokenHash),
                System.Text.Encoding.UTF8.GetBytes(tokenHash))
                ? row.ToRecord()
                : null;
    }

    public async Task<AdminApiTokenRecord?> FindAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            $"SELECT {Columns} FROM [AdminApiTokens] WHERE [TokenId] = @TokenId",
            new { TokenId = tokenId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<AdminApiTokenRecord>> ListByOwnerAsync(
        Guid ownerAdminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(Sql(
            $"SELECT {Columns} FROM [AdminApiTokens] WHERE [OwnerAdminUserId] = @Owner "
            + "ORDER BY [CreatedAt] DESC, [TokenId]",
            new { Owner = ownerAdminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(row => row.ToRecord()).ToList();
    }

    public async Task<int> CountActiveAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [AdminApiTokens] "
            + "WHERE [OwnerAdminUserId] = @Owner AND [RevokedAt] IS NULL AND [ExpiresAt] > @Now",
            new { Owner = ownerAdminUserId, Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(
        Guid tokenId,
        Guid? ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **持ち主の組で絞る**（他人の識別子を渡されても失効しない）。**すでに失効しているものは書き換えない**
        // （最初の失効の理由と日時を残す）
        var sql = "UPDATE [AdminApiTokens] SET [RevokedAt] = @Now, [RevokedBy] = @RevokedBy, [RevokeReason] = @Reason "
            + "WHERE [TokenId] = @TokenId AND [RevokedAt] IS NULL"
            + (ownerAdminUserId is null ? string.Empty : " AND [OwnerAdminUserId] = @Owner");
        return await connection.ExecuteAsync(Sql(
            sql,
            new
            {
                TokenId = tokenId,
                Owner = ownerAdminUserId,
                Now = DbTime.ForDb(nowUtc),
                RevokedBy = revokedBy,
                Reason = reason,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<AdminApiTokenRecord>> RevokeAllForOwnerAsync(
        Guid ownerAdminUserId,
        DateTime nowUtc,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **対象は「いま使えるもの」だけ。** 期限切れはすでに使えないので、失効の印は付けない
        // （一覧で「期限切れ」と「失効」を混ぜない）。
        // 先に対象を読んでから 1 本ずつ失効させる（同時に別の失効が走っても、書き換えるのは未失効の行だけ）
        var targets = await connection.QueryAsync<Row>(Sql(
            $"SELECT {Columns} FROM [AdminApiTokens] "
            + "WHERE [OwnerAdminUserId] = @Owner AND [RevokedAt] IS NULL AND [ExpiresAt] > @Now",
            new { Owner = ownerAdminUserId, Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var revoked = new List<AdminApiTokenRecord>();
        foreach (var target in targets)
        {
            var changed = await connection.ExecuteAsync(Sql(
                "UPDATE [AdminApiTokens] SET [RevokedAt] = @Now, [RevokedBy] = @RevokedBy, [RevokeReason] = @Reason "
                + "WHERE [TokenId] = @TokenId AND [RevokedAt] IS NULL",
                new
                {
                    target.TokenId,
                    Now = DbTime.ForDb(nowUtc),
                    RevokedBy = revokedBy,
                    Reason = reason,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (changed > 0)
            {
                revoked.Add(target.ToRecord());
            }
        }

        return revoked;
    }

    public async Task<bool> TouchLastUsedAsync(
        Guid tokenId,
        DateTime nowUtc,
        string? ipAddress,
        TimeSpan minInterval,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **間引く。** 読み取りの多いスクリプトで、要求のたびに書き込みを増やさない
        return await connection.ExecuteAsync(Sql(
            "UPDATE [AdminApiTokens] SET [LastUsedAt] = @Now, [LastUsedIp] = @Ip "
            + "WHERE [TokenId] = @TokenId AND ([LastUsedAt] IS NULL OR [LastUsedAt] <= @Threshold)",
            new
            {
                TokenId = tokenId,
                Now = DbTime.ForDb(nowUtc),
                Ip = ipAddress is { Length: > 64 } ? ipAddress[..64] : ipAddress,
                Threshold = DbTime.ForDb(nowUtc - minInterval),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
    }

    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む**（<c>SqlDialect.Format</c> が各 RDBMS の引用符へ書き換える）。</summary>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>行。**単独の値ではなく行として読む**（MySQL・SQLite の型変換に引っ掛からないようにする）。</summary>
    private sealed class Row
    {
        public Guid TokenId { get; set; }

        /// <summary>引いた値の突き合わせにだけ使う。<b>記録（<see cref="AdminApiTokenRecord"/>）へは載せない。</b></summary>
        public string TokenHash { get; set; } = "";

        public Guid OwnerAdminUserId { get; set; }

        public string Name { get; set; } = "";

        public string Prefix { get; set; } = "";

        public string Scopes { get; set; } = "";

        public string? IpAllowList { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        public Guid? RevokedBy { get; set; }

        public string? RevokeReason { get; set; }

        public DateTime? LastUsedAt { get; set; }

        public string? LastUsedIp { get; set; }

        public AdminApiTokenRecord ToRecord() => new(
            TokenId,
            OwnerAdminUserId,
            Name,
            Prefix,
            Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            string.IsNullOrWhiteSpace(IpAllowList)
                ? []
                : IpAllowList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            DbTime.AsUtc(CreatedAt),
            DbTime.AsUtc(ExpiresAt),
            DbTime.AsUtc(RevokedAt),
            RevokedBy,
            RevokeReason,
            DbTime.AsUtc(LastUsedAt),
            LastUsedIp);
    }
}
