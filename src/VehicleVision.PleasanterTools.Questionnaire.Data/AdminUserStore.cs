using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理者の役割。</summary>
public enum AdminRole
{
    /// <summary>アンケートの作成と編集ができる。</summary>
    Editor = 0,

    /// <summary>加えて管理者の追加・削除ができる。</summary>
    Administrator = 1,
}

/// <summary>管理者 1 人分。</summary>
/// <remarks>
/// **秘密は復元できる形で持たない。**
/// <see cref="PasswordHash"/> はハッシュのみ、
/// <see cref="TotpSecretEncrypted"/> は暗号化した形のみ
/// （<c>_documents/データモデル設計.md</c> 2.6）。
/// </remarks>
public sealed record AdminUser
{
    public required Guid AdminUserId { get; init; }
    public required string LoginId { get; init; }
    public required string PasswordHash { get; init; }
    public required AdminRole Role { get; init; }
    public bool IsDisabled { get; init; }
    public string? TotpSecretEncrypted { get; init; }
    public DateTime? TotpEnabledAt { get; init; }

    /// <summary>**止め忘れた管理者を見つける唯一の手掛かり。**</summary>
    public DateTime? LastLoginAt { get; init; }

    public int FailedLoginCount { get; init; }

    /// <summary>直前に通した時間枠。**同じ枠を二度通さないための印。**</summary>
    public long? TotpLastTimeStep { get; init; }

    /// <summary>管理画面を出す言語。<c>null</c> は「まだ選んでいない」。</summary>
    /// <remarks>
    /// **画面の初期値を決めるためのもの。** サーバの応答の言語はここでは決めない
    /// （<c>_documents/多言語対応方針.md</c> 2 章）。
    /// </remarks>
    public string? Language { get; init; }


    public DateTime? LockedUntil { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>2 要素の登録が済んでいるか。</summary>
    public bool HasTotp => TotpSecretEncrypted is { Length: > 0 } && TotpEnabledAt is not null;
}

/// <summary>復旧コード 1 本分（照合に要る分だけ）。</summary>
public sealed record RecoveryCodeRow
{
    public required Guid RecoveryCodeId { get; init; }
    public required string CodeHash { get; init; }
}

/// <summary>管理者と復旧コードの読み書き。</summary>
public interface IAdminUserStore
{
    Task<AdminUser?> FindByLoginIdAsync(string loginId, CancellationToken cancellationToken = default);

    Task<AdminUser?> FindByIdAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>管理者が 1 人も居ないか。初期設定の画面を出すかどうかの判断に使う。</summary>
    Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default);

    Task CreateAsync(AdminUser user, CancellationToken cancellationToken = default);

    Task UpdatePasswordHashAsync(
        Guid adminUserId,
        string passwordHash,
        CancellationToken cancellationToken = default);

    /// <summary>2 要素を有効にする。<paramref name="secretEncrypted"/> は**暗号化済みの文字列**。</summary>
    Task EnableTotpAsync(
        Guid adminUserId,
        string secretEncrypted,
        CancellationToken cancellationToken = default);

    Task SetDisabledAsync(Guid adminUserId, bool isDisabled, CancellationToken cancellationToken = default);

    /// <summary>管理画面を出す言語を決める。<c>null</c> で「選んでいない」に戻す。</summary>
    Task SetLanguageAsync(
        Guid adminUserId,
        string? language,
        CancellationToken cancellationToken = default);

    /// <summary>止める。**最後の <see cref="AdminRole.Administrator"/> は止めさせない。**</summary>
    /// <returns>
    /// 止められたかどうか。<c>false</c> なら**他に入れる管理者が居なかった**。
    /// 「入れる」は、有効かつ**一度でもログインしたことがある**こと。
    /// </returns>
    Task<bool> TryDisableAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>役割を変える。**最後の <see cref="AdminRole.Administrator"/> は降格させない。**</summary>
    /// <returns>変えられたかどうか。<c>false</c> なら**他に入れる管理者が居なかった**。</returns>
    Task<bool> TrySetRoleAsync(
        Guid adminUserId,
        AdminRole role,
        CancellationToken cancellationToken = default);

    /// <summary>ログインが通ったことを記録し、失敗回数と締め出しを消す。</summary>
    Task RecordSuccessAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>失敗を数える。</summary>
    /// <returns>加算後の失敗回数。</returns>
    Task<int> RecordFailureAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>指定の時刻まで締め出す。</summary>
    Task LockAsync(Guid adminUserId, DateTime lockedUntilUtc, CancellationToken cancellationToken = default);

    /// <summary>復旧コードを**入れ替える**。前のものは消える。</summary>
    Task ReplaceRecoveryCodesAsync(
        Guid adminUserId,
        IReadOnlyList<string> codeHashes,
        CancellationToken cancellationToken = default);

    /// <summary>まだ使っていない復旧コードを返す。</summary>
    Task<IReadOnlyList<RecoveryCodeRow>> ListUnusedRecoveryCodesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default);

    /// <summary>復旧コードを使用済みにする。</summary>
    /// <returns>
    /// 使用済みにできたかどうか。**同時に 2 回来ても 1 回しか <c>true</c> にならない。**
    /// </returns>
    Task<bool> TryConsumeRecoveryCodeAsync(Guid recoveryCodeId, CancellationToken cancellationToken = default);

    /// <summary>使い捨てパスワードの時間枠を使う。</summary>
    /// <returns>
    /// 使えたかどうか。**前に通した枠と同じか、それより前なら <c>false</c>。**
    /// 盗み見られた数字の二度目を弾く。
    /// </returns>
    Task<bool> TryConsumeTotpTimeStepAsync(
        Guid adminUserId,
        long timeStep,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class AdminUserStore(IDbConnectionFactory connectionFactory) : IAdminUserStore
{
    private static readonly string[] SelectColumns =
    [
        "AdminUserId", "LoginId", "PasswordHash", "Role", "IsDisabled", "TotpSecretEncrypted",
        "TotpEnabledAt", "LastLoginAt", "FailedLoginCount", "TotpLastTimeStep", "LockedUntil",
        "Language", "CreatedAt", "UpdatedAt",
    ];

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<AdminUser?> FindByLoginIdAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminUser>(Sql(
            $"SELECT {Select()} FROM [AdminUsers] WHERE [LoginId] = @LoginId",
            new { LoginId = loginId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AdminUser?> FindByIdAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminUser>(Sql(
            $"SELECT {Select()} FROM [AdminUsers] WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AdminUser>(Sql(
            $"SELECT {Select()} FROM [AdminUsers] ORDER BY [LoginId]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<long>(Sql(
            "SELECT COUNT(*) FROM [AdminUsers]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count == 0;
    }

    public async Task CreateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AdminUsers] ("
            + "[AdminUserId], [LoginId], [PasswordHash], [Role], "
            + "[IsDisabled], [TotpSecretEncrypted], [TotpEnabledAt], "
            + "[FailedLoginCount], [CreatedAt], [UpdatedAt]) "
            + "VALUES (@AdminUserId, @LoginId, @PasswordHash, @Role, "
            + "@IsDisabled, @TotpSecretEncrypted, @TotpEnabledAt, 0, @Now, @Now)",
            new
            {
                user.AdminUserId,
                user.LoginId,
                user.PasswordHash,
                Role = (int)user.Role,
                user.IsDisabled,
                user.TotpSecretEncrypted,
                user.TotpEnabledAt,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task UpdatePasswordHashAsync(
        Guid adminUserId,
        string passwordHash,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [PasswordHash] = @PasswordHash, "
            + "[UpdatedAt] = @Now WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, PasswordHash = passwordHash, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task EnableTotpAsync(
        Guid adminUserId,
        string secretEncrypted,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [TotpSecretEncrypted] = @Secret, "
            + "[TotpEnabledAt] = @Now, [UpdatedAt] = @Now "
            + "WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, Secret = secretEncrypted, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task SetDisabledAsync(
        Guid adminUserId,
        bool isDisabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [IsDisabled] = @IsDisabled, "
            + "[UpdatedAt] = @Now WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, IsDisabled = isDisabled, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task SetLanguageAsync(
        Guid adminUserId,
        string? language,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [Language] = @Language, "
            + "[UpdatedAt] = @Now WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, Language = language, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task<bool> TryDisableAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        // **最後の 1 人でなければ止める。** 既に止まっている相手は、人数を減らさないので通す
        GuardedUpdateAsync(
            "[IsDisabled] = @Disabled",
            $"([Role] <> @Administrator OR [IsDisabled] = @Disabled OR {OtherAdministratorExists()})",
            new { AdminUserId = adminUserId },
            cancellationToken);

    public Task<bool> TrySetRoleAsync(
        Guid adminUserId,
        AdminRole role,
        CancellationToken cancellationToken = default) =>
        // **降格でなければ素通し。** 昇格や、元から Editor の相手は誰も締め出さない
        GuardedUpdateAsync(
            "[Role] = @Role",
            "(@Role = @Administrator OR [Role] <> @Administrator "
            + $"OR [IsDisabled] = @Disabled OR {OtherAdministratorExists()})",
            new { AdminUserId = adminUserId, Role = (int)role },
            cancellationToken);

    /// <summary>他に**実際に入れる** <see cref="AdminRole.Administrator"/> が居るかを見る条件。</summary>
    /// <remarks>
    /// <para>
    /// **一度もログインしていない管理者を当てにしない**（<c>LastLoginAt IS NULL</c> は数えない）。
    /// 招待しただけの管理者は**誰も知らないパスワード**しか持たないので、
    /// 頭数に入れると「招いたが受け取っていない相手」を頼りに
    /// 最後の 1 人を止められてしまう。**それは締め出しそのもの。**
    /// </para>
    /// <para>
    /// この条件は**厳しい側に外れる**。まだ一度も入っていない相手が居る間は、
    /// 止める操作が断られる。**断るのは安全側**なので、そちらへ寄せた。
    /// </para>
    /// <para>
    /// **副問い合わせを派生表で包んでいるのは MySQL のため。**
    /// 「更新する表を副問い合わせで直接読む」ことができない（ERROR 1093）。
    /// 派生表にすると 3 者とも通る。
    /// </para>
    /// <para>
    /// **1 文にまとめているのは、読んでから書く形を避けるため。**
    /// ただし PostgreSQL / MySQL は読みが待たないので、
    /// **2 人が同時に互いを止めれば理論上は擦り抜ける。**
    /// 管理者の人数と操作の頻度からは実際上起き得ないと判断し、
    /// 直列化する取引まではしていない。
    /// </para>
    /// </remarks>
    private string OtherAdministratorExists() =>
        "EXISTS (SELECT 1 FROM (SELECT [AdminUserId], [Role], [IsDisabled], "
        + "[LastLoginAt] FROM [AdminUsers]) AS [other] "
        + "WHERE [other].[AdminUserId] <> @AdminUserId "
        + "AND [other].[Role] = @Administrator "
        + "AND [other].[IsDisabled] = @Enabled "
        + "AND [other].[LastLoginAt] IS NOT NULL)";

    /// <summary>誰も入れなくならないことを確かめてから更新する。</summary>
    private async Task<bool> GuardedUpdateAsync(
        string assignment,
        string guard,
        object parameters,
        CancellationToken cancellationToken)
    {
        var arguments = new DynamicParameters(parameters);
        arguments.Add("Now", DbTime.UtcNowTruncated());
        arguments.Add("Administrator", (int)AdminRole.Administrator);
        arguments.Add("Disabled", true);
        arguments.Add("Enabled", false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            $"UPDATE [AdminUsers] SET {assignment}, [UpdatedAt] = @Now "
            + $"WHERE [AdminUserId] = @AdminUserId AND {guard}",
            arguments,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    public Task RecordSuccessAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [LastLoginAt] = @Now, "
            + "[FailedLoginCount] = 0, [LockedUntil] = NULL, [UpdatedAt] = @Now "
            + "WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public async Task<int> RecordFailureAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **DB 側で足す。** 読んでから書くと、同時に来た試行を数え落とす
        await connection.ExecuteAsync(Sql(
            "UPDATE [AdminUsers] SET [FailedLoginCount] = [FailedLoginCount] + 1, "
            + "[UpdatedAt] = @Now WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT [FailedLoginCount] FROM [AdminUsers] "
            + "WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task LockAsync(
        Guid adminUserId,
        DateTime lockedUntilUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AdminUsers] SET [LockedUntil] = @LockedUntil, "
            + "[UpdatedAt] = @Now WHERE [AdminUserId] = @AdminUserId",
            new
            {
                AdminUserId = adminUserId,
                LockedUntil = DbTime.ForDb(lockedUntilUtc),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken);

    public async Task ReplaceRecoveryCodesAsync(
        Guid adminUserId,
        IReadOnlyList<string> codeHashes,
        CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **入れ替える。** 古いものを残すと、配り直した意味が無くなる
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [AdminRecoveryCodes] WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var codeHash in codeHashes)
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [AdminRecoveryCodes] ("
                + "[RecoveryCodeId], [AdminUserId], [CodeHash], [CreatedAt]) "
                + "VALUES (@RecoveryCodeId, @AdminUserId, @CodeHash, @Now)",
                new
                {
                    RecoveryCodeId = Guid.NewGuid(),
                    AdminUserId = adminUserId,
                    CodeHash = codeHash,
                    Now = now,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecoveryCodeRow>> ListUnusedRecoveryCodesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RecoveryCodeRow>(Sql(
            "SELECT [RecoveryCodeId], [CodeHash] FROM [AdminRecoveryCodes] "
            + "WHERE [AdminUserId] = @AdminUserId AND [UsedAt] IS NULL",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<bool> TryConsumeRecoveryCodeAsync(
        Guid recoveryCodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **未使用の行だけを更新し、更新できた件数で判断する。**
        // 読んでから書くと、同時に来た 2 つが両方とも通ってしまう
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [AdminRecoveryCodes] SET [UsedAt] = @Now "
            + "WHERE [RecoveryCodeId] = @RecoveryCodeId AND [UsedAt] IS NULL",
            new { RecoveryCodeId = recoveryCodeId, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<bool> TryConsumeTotpTimeStepAsync(
        Guid adminUserId,
        long timeStep,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **前に進むときだけ更新する。** 読んでから書くと、同時に来た 2 つが両方とも通る
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [AdminUsers] SET [TotpLastTimeStep] = @TimeStep "
            + "WHERE [AdminUserId] = @AdminUserId "
            + "AND ([TotpLastTimeStep] IS NULL OR [TotpLastTimeStep] < @TimeStep)",
            new { AdminUserId = adminUserId, TimeStep = timeStep },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>読み出す列の並び。**引用は SqlDialect.Format が行う。**</summary>
    private static string Select() =>
        string.Join(", ", SelectColumns.Select(name => "[" + name + "]"));

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }


    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む。**</summary>
    /// <remarks>
    /// **生の文字列連結をしない**ための口（<c>SqlDialect.Format</c>）。
    /// 角括弧の中だけが RDBMS ごとの引用符へ書き換わる。
    /// </remarks>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, transaction, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
