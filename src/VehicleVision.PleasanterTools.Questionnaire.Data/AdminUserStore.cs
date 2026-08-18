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
        "CreatedAt", "UpdatedAt",
    ];

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<AdminUser?> FindByLoginIdAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminUser>(new CommandDefinition(
            $"SELECT {Select()} FROM {Q("AdminUsers")} WHERE {Q("LoginId")} = @LoginId",
            new { LoginId = loginId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AdminUser?> FindByIdAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminUser>(new CommandDefinition(
            $"SELECT {Select()} FROM {Q("AdminUsers")} WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AdminUser>(new CommandDefinition(
            $"SELECT {Select()} FROM {Q("AdminUsers")} ORDER BY {Q("LoginId")}",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {Q("AdminUsers")}",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count == 0;
    }

    public async Task CreateAsync(AdminUser user, CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO {Q("AdminUsers")} ("
            + $"{Q("AdminUserId")}, {Q("LoginId")}, {Q("PasswordHash")}, {Q("Role")}, "
            + $"{Q("IsDisabled")}, {Q("TotpSecretEncrypted")}, {Q("TotpEnabledAt")}, "
            + $"{Q("FailedLoginCount")}, {Q("CreatedAt")}, {Q("UpdatedAt")}) "
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
            $"UPDATE {Q("AdminUsers")} SET {Q("PasswordHash")} = @PasswordHash, "
            + $"{Q("UpdatedAt")} = @Now WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId, PasswordHash = passwordHash, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task EnableTotpAsync(
        Guid adminUserId,
        string secretEncrypted,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"UPDATE {Q("AdminUsers")} SET {Q("TotpSecretEncrypted")} = @Secret, "
            + $"{Q("TotpEnabledAt")} = @Now, {Q("UpdatedAt")} = @Now "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId, Secret = secretEncrypted, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task SetDisabledAsync(
        Guid adminUserId,
        bool isDisabled,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"UPDATE {Q("AdminUsers")} SET {Q("IsDisabled")} = @IsDisabled, "
            + $"{Q("UpdatedAt")} = @Now WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId, IsDisabled = isDisabled, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public Task<bool> TryDisableAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        // **最後の 1 人でなければ止める。** 既に止まっている相手は、人数を減らさないので通す
        GuardedUpdateAsync(
            $"{Q("IsDisabled")} = @Disabled",
            $"({Q("Role")} <> @Administrator OR {Q("IsDisabled")} = @Disabled OR {OtherAdministratorExists()})",
            new { AdminUserId = adminUserId },
            cancellationToken);

    public Task<bool> TrySetRoleAsync(
        Guid adminUserId,
        AdminRole role,
        CancellationToken cancellationToken = default) =>
        // **降格でなければ素通し。** 昇格や、元から Editor の相手は誰も締め出さない
        GuardedUpdateAsync(
            $"{Q("Role")} = @Role",
            $"(@Role = @Administrator OR {Q("Role")} <> @Administrator "
            + $"OR {Q("IsDisabled")} = @Disabled OR {OtherAdministratorExists()})",
            new { AdminUserId = adminUserId, Role = (int)role },
            cancellationToken);

    /// <summary>他に**実際に入れる** <see cref="AdminRole.Administrator"/> が居るかを見る条件。</summary>
    /// <remarks>
    /// <para>
    /// **一度もログインしていない管理者を当てにしない**（<c>LastLoginAt IS NULL</c> は数えない）。
    /// 招待しただけの管理者は**誰も知らない合言葉**しか持たないので、
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
        $"EXISTS (SELECT 1 FROM (SELECT {Q("AdminUserId")}, {Q("Role")}, {Q("IsDisabled")}, "
        + $"{Q("LastLoginAt")} FROM {Q("AdminUsers")}) AS {Q("other")} "
        + $"WHERE {Q("other")}.{Q("AdminUserId")} <> @AdminUserId "
        + $"AND {Q("other")}.{Q("Role")} = @Administrator "
        + $"AND {Q("other")}.{Q("IsDisabled")} = @Enabled "
        + $"AND {Q("other")}.{Q("LastLoginAt")} IS NOT NULL)";

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
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("AdminUsers")} SET {assignment}, {Q("UpdatedAt")} = @Now "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId AND {guard}",
            arguments,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    public Task RecordSuccessAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"UPDATE {Q("AdminUsers")} SET {Q("LastLoginAt")} = @Now, "
            + $"{Q("FailedLoginCount")} = 0, {Q("LockedUntil")} = NULL, {Q("UpdatedAt")} = @Now "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId, Now = DbTime.UtcNowTruncated() },
            cancellationToken);

    public async Task<int> RecordFailureAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **DB 側で足す。** 読んでから書くと、同時に来た試行を数え落とす
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("AdminUsers")} SET {Q("FailedLoginCount")} = {Q("FailedLoginCount")} + 1, "
            + $"{Q("UpdatedAt")} = @Now WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT {Q("FailedLoginCount")} FROM {Q("AdminUsers")} "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public Task LockAsync(
        Guid adminUserId,
        DateTime lockedUntilUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            $"UPDATE {Q("AdminUsers")} SET {Q("LockedUntil")} = @LockedUntil, "
            + $"{Q("UpdatedAt")} = @Now WHERE {Q("AdminUserId")} = @AdminUserId",
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
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {Q("AdminRecoveryCodes")} WHERE {Q("AdminUserId")} = @AdminUserId",
            new { AdminUserId = adminUserId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var codeHash in codeHashes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO {Q("AdminRecoveryCodes")} ("
                + $"{Q("RecoveryCodeId")}, {Q("AdminUserId")}, {Q("CodeHash")}, {Q("CreatedAt")}) "
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
        var rows = await connection.QueryAsync<RecoveryCodeRow>(new CommandDefinition(
            $"SELECT {Q("RecoveryCodeId")}, {Q("CodeHash")} FROM {Q("AdminRecoveryCodes")} "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId AND {Q("UsedAt")} IS NULL",
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
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("AdminRecoveryCodes")} SET {Q("UsedAt")} = @Now "
            + $"WHERE {Q("RecoveryCodeId")} = @RecoveryCodeId AND {Q("UsedAt")} IS NULL",
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
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("AdminUsers")} SET {Q("TotpLastTimeStep")} = @TimeStep "
            + $"WHERE {Q("AdminUserId")} = @AdminUserId "
            + $"AND ({Q("TotpLastTimeStep")} IS NULL OR {Q("TotpLastTimeStep")} < @TimeStep)",
            new { AdminUserId = adminUserId, TimeStep = timeStep },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    private string Select() => string.Join(", ", SelectColumns.Select(Q));

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private string Q(string identifier) => SqlDialect.Quote(Provider, identifier);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
