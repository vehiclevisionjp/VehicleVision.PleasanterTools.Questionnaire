using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>管理者の招待 1 通分。</summary>
/// <remarks>
/// **元のトークンは持たない。** <see cref="TokenHash"/> だけを置き、
/// 表を読めた人が招待を使えないようにする。
/// </remarks>
public sealed record AdminInvitation
{
    public required Guid InvitationId { get; init; }
    public required Guid AdminUserId { get; init; }
    public required string TokenHash { get; init; }

    /// <summary>これを過ぎたら使えない。**期限の無い招待は作らない。**</summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>使った時刻。**入っていればもう使えない。**</summary>
    public DateTime? UsedAt { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>誰が招いたか。</summary>
    public Guid? CreatedBy { get; init; }
}

/// <summary>管理者の招待の読み書き。</summary>
public interface IAdminInvitationStore
{
    /// <summary>招待を**入れ替える**。同じ相手宛ての未使用の招待は消える。</summary>
    /// <remarks>
    /// **前の招待を残さない。** 出し直したのに古い方も通るなら、出し直した意味が無い。
    /// 使用済みの行は残す（使われた事実を消さないため）。
    /// </remarks>
    Task ReplaceAsync(AdminInvitation invitation, CancellationToken cancellationToken = default);

    /// <summary>ハッシュで招待を引く。</summary>
    Task<AdminInvitation?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>招待を使用済みにする。</summary>
    /// <returns>
    /// 使用済みにできたかどうか。**同時に 2 回来ても 1 回しか <c>true</c> にならない。**
    /// </returns>
    Task<bool> TryConsumeAsync(Guid invitationId, CancellationToken cancellationToken = default);

    /// <summary>まだ使える招待が残っている相手を返す。</summary>
    /// <remarks>一覧に「まだ受け取っていない」を出すために使う。</remarks>
    Task<IReadOnlyList<Guid>> ListPendingAdminUserIdsAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>未使用の招待を取り消す。</summary>
    /// <remarks>**止めた利用者宛ての招待は消す。** 止めたのに合言葉を決められては困る。</remarks>
    Task RevokeUnusedAsync(Guid adminUserId, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class AdminInvitationStore(IDbConnectionFactory connectionFactory) : IAdminInvitationStore
{
    private const string Table = "AdminInvitations";

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task ReplaceAsync(
        AdminInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **未使用の分だけ消す。** 使用済みの行は、使われた事実として残す
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {Q(Table)} WHERE {Q("AdminUserId")} = @AdminUserId "
            + $"AND {Q("UsedAt")} IS NULL",
            new { invitation.AdminUserId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO {Q(Table)} ("
            + $"{Q("InvitationId")}, {Q("AdminUserId")}, {Q("TokenHash")}, {Q("ExpiresAt")}, "
            + $"{Q("CreatedAt")}, {Q("CreatedBy")}) "
            + "VALUES (@InvitationId, @AdminUserId, @TokenHash, @ExpiresAt, @Now, @CreatedBy)",
            new
            {
                invitation.InvitationId,
                invitation.AdminUserId,
                invitation.TokenHash,
                ExpiresAt = DbTime.ForDb(invitation.ExpiresAt),
                Now = DbTime.UtcNowTruncated(),
                invitation.CreatedBy,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdminInvitation?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<AdminInvitation>(new CommandDefinition(
            $"SELECT {Q("InvitationId")}, {Q("AdminUserId")}, {Q("TokenHash")}, {Q("ExpiresAt")}, "
            + $"{Q("UsedAt")}, {Q("CreatedAt")}, {Q("CreatedBy")} FROM {Q(Table)} "
            + $"WHERE {Q("TokenHash")} = @TokenHash",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> TryConsumeAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **未使用の行だけを更新し、更新できた件数で判断する。**
        // 読んでから書くと、同時に来た 2 つが両方とも通ってしまう
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q(Table)} SET {Q("UsedAt")} = @Now "
            + $"WHERE {Q("InvitationId")} = @InvitationId AND {Q("UsedAt")} IS NULL",
            new { InvitationId = invitationId, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<IReadOnlyList<Guid>> ListPendingAdminUserIdsAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Guid>(new CommandDefinition(
            $"SELECT {Q("AdminUserId")} FROM {Q(Table)} "
            + $"WHERE {Q("UsedAt")} IS NULL AND {Q("ExpiresAt")} > @Now",
            new { Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task RevokeUnusedAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {Q(Table)} WHERE {Q("AdminUserId")} = @AdminUserId "
            + $"AND {Q("UsedAt")} IS NULL",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private string Q(string identifier) => SqlDialect.Quote(Provider, identifier);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
