using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>残す管理操作 1 件。</summary>
/// <param name="OccurredAt">起きた時刻。</param>
/// <param name="AdminUserId">誰が。**認証を通っていない試みでは <c>null</c>。**</param>
/// <param name="Action">何を。<c>POST /api/admin/users/{adminUserId}/role</c> のような形。</param>
/// <param name="StatusCode">
/// 結果の HTTP 状態。**失敗も残す。**
/// **NULL 可**。この列が無かった頃に書かれた行を「成功」と読ませないため。
/// </param>
/// <param name="TargetType">対象の種類（<c>AdminUser</c> / <c>Survey</c>）。</param>
/// <param name="TargetId">対象の識別子。</param>
/// <param name="DetailJson">補足。**資格情報・回答本文を入れない。**</param>
/// <param name="IpAddress">送信元。</param>
public sealed record AuditEntry(
    DateTime OccurredAt,
    Guid? AdminUserId,
    string Action,
    int? StatusCode,
    string? TargetType = null,
    string? TargetId = null,
    string? DetailJson = null,
    string? IpAddress = null);

/// <summary>管理操作の記録を読み書きする。</summary>
/// <remarks>
/// <para>
/// **回答本文・資格情報・<c>ResponseToken</c> を入れない**
/// （<c>_documents/データモデル設計.md</c> 2.6）。
/// **入れないことを設計で担保する。** 記録するのは要求の宛先・結果・対象の識別子だけで、
/// 要求本文には触れない。触らない限り、パスワードも招待のパスワードも入りようがない。
/// </para>
/// <para>
/// **失敗した操作も残す。** 成功だけ残すと、試みられたことが分からない。
/// </para>
/// </remarks>
public interface IAuditLogStore
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AuditLogView>> ListAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>指定した時刻より古い記録を消す。</summary>
    /// <remarks>
    /// **消さないと増え続ける。** 監査ログは 1 操作 1 行で、
    /// 画面から読むたびに大きくなった表を走査することになる
    /// （<c>_documents/データモデル設計.md</c>）。
    /// </remarks>
    /// <returns>消した件数。</returns>
    Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default);
}

/// <summary>記録を読むときの絞り込み。</summary>
/// <remarks>
/// **既定は「直近を新しい順に」。** 何も指定しなくても使えるようにしてある。
/// </remarks>
public sealed record AuditLogQuery
{
    /// <summary>読む件数の上限。</summary>
    public int Limit { get; init; } = 100;

    /// <summary>読み飛ばす件数。**ページ送り用。**</summary>
    public int Offset { get; init; }

    /// <summary>この時刻以降。</summary>
    public DateTime? From { get; init; }

    /// <summary>この時刻より前。</summary>
    public DateTime? To { get; init; }

    /// <summary>この管理者の操作だけ。</summary>
    public Guid? AdminUserId { get; init; }

    /// <summary>断られた操作だけ（400 以上）。</summary>
    /// <remarks>
    /// **結果を持たない行（<c>StatusCode</c> が NULL）は含めない。**
    /// 列を足す前に書かれた行で、成功とも失敗とも言えない。
    /// </remarks>
    public bool FailedOnly { get; init; }

    /// <summary>操作の名前に含まれる文字。</summary>
    public string? ActionContains { get; init; }
}

/// <summary>画面に出す記録 1 件。</summary>
/// <param name="AdminLoginId">
/// 操作した管理者の名前。**もう居ない管理者の行では <c>null</c>。**
/// 記録は残っても、その人の行が消えていることはある。
/// </param>
public sealed record AuditLogView(
    DateTime OccurredAt,
    Guid? AdminUserId,
    string? AdminLoginId,
    string Action,
    int? StatusCode,
    string? TargetType,
    string? TargetId,
    string? DetailJson,
    string? IpAddress);

/// <summary>Dapper を使った実装。</summary>
public sealed class AuditLogStore(IDbConnectionFactory connectionFactory) : IAuditLogStore
{
    /// <summary>列の桁。**溢れさせずに切り詰める。**</summary>
    /// <remarks>
    /// **記録できなかったより、切り詰めた方がまし。**
    /// 桁溢れで例外にすると、操作そのものは通ったのに記録だけが落ちる。
    /// </remarks>
    private const int ActionLength = 128;

    private const int TargetLength = 128;

    private const int IpAddressLength = 64;

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AuditLogs] "
            + "([AuditLogId], [OccurredAt], [AdminUserId], [Action], [StatusCode], "
            + " [TargetType], [TargetId], [DetailJson], [IpAddress]) "
            + "VALUES (@AuditLogId, @OccurredAt, @AdminUserId, @Action, @StatusCode, "
            + "        @TargetType, @TargetId, @DetailJson, @IpAddress)",
            new
            {
                AuditLogId = Guid.NewGuid(),
                OccurredAt = DbTime.ForDb(entry.OccurredAt),
                entry.AdminUserId,
                Action = Trim(entry.Action, ActionLength),
                entry.StatusCode,
                TargetType = Trim(entry.TargetType, TargetLength),
                TargetId = Trim(entry.TargetId, TargetLength),
                entry.DetailJson,
                IpAddress = Trim(entry.IpAddress, IpAddressLength),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditLogView>> ListAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        // **組み立てるのは条件の「形」だけで、値は必ず引数で渡す。**
        // 形は下に並んでいる定数しか入らないので、利用者の入力が SQL にならない
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Limit", query.Limit);
        parameters.Add("Offset", query.Offset);

        if (query.From is { } from)
        {
            conditions.Add("a.[OccurredAt] >= @From");
            parameters.Add("From", DbTime.ForDb(from));
        }

        if (query.To is { } to)
        {
            conditions.Add("a.[OccurredAt] < @To");
            parameters.Add("To", DbTime.ForDb(to));
        }

        if (query.AdminUserId is { } adminUserId)
        {
            conditions.Add("a.[AdminUserId] = @AdminUserId");
            parameters.Add("AdminUserId", adminUserId);
        }

        if (query.FailedOnly)
        {
            // **結果を持たない行は含めない。** 成功とも失敗とも言えない
            conditions.Add("a.[StatusCode] >= 400");
        }

        if (!string.IsNullOrWhiteSpace(query.ActionContains))
        {
            conditions.Add($"a.[Action] LIKE @ActionLike ESCAPE '{SqlDialect.LikeEscape}'");
            parameters.Add(
                "ActionLike", $"%{SqlDialect.EscapeLike(query.ActionContains.Trim())}%");
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions) + " ";

        // **誰がやったかは名前で見せる。** GUID のままでは読めない。
        // **LEFT JOIN。** もう居ない管理者の記録も残す
        var sql =
            "SELECT a.[OccurredAt], a.[AdminUserId], u.[LoginId] AS [AdminLoginId], "
            + "       a.[Action], a.[StatusCode], a.[TargetType], a.[TargetId], "
            + "       a.[DetailJson], a.[IpAddress] "
            + "FROM [AuditLogs] a LEFT JOIN [AdminUsers] u ON u.[AdminUserId] = a.[AdminUserId] "
            + where
            + "ORDER BY a.[OccurredAt] DESC, a.[AuditLogId] DESC "
            + SqlDialect.Page(Provider);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AuditLogView>(
            Sql(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AuditLogs] WHERE [OccurredAt] < @Threshold",
            new { Threshold = DbTime.ForDb(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>桁に収まるよう切り詰める。</summary>
    private static string? Trim(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

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
