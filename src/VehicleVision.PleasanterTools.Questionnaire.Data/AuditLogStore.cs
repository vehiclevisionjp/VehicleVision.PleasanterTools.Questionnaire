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
/// 要求本文には触れない。触らない限り、合言葉も招待の合言葉も入りようがない。
/// </para>
/// <para>
/// **失敗した操作も残す。** 成功だけ残すと、試みられたことが分からない。
/// </para>
/// </remarks>
public interface IAuditLogStore
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AuditEntry>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

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

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **件数の絞り方が 3 者で違うので、SQL は SqlDialect に置いてある**
        var rows = await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            SqlDialect.ListAuditLogs(Provider),
            new { Limit = limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
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
