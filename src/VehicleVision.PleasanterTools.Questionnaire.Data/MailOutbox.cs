using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>メールの送信待ちの状態。</summary>
/// <remarks>**<c>Sent</c> は持たない。送れたら行を消す**（<c>ResponseStatus</c> と同じ）。</remarks>
public enum MailStatus
{
    Pending = 0,
    Sending = 1,
    DeadLetter = 2,
}

/// <summary>送信待ちのメール 1 件。</summary>
/// <remarks>
/// ⚠️ **<see cref="PayloadProtected"/> は暗号化されたまま。**
/// 宛先も本文もここでは読めない。**復号は送信の直前だけ**（Issue #189）。
/// </remarks>
public sealed record PendingMail(
    Guid MailId,
    int Kind,
    Guid SurveyId,
    string PayloadProtected,
    int RetryCount);

/// <summary>メールの滞留の状況。**管理画面に出す数字。**</summary>
/// <remarks>**宛先も件名も持たない。** 出すのは件数と時刻だけ（回答の滞留と同じ）。</remarks>
public sealed record MailOutboxStatus(
    int PendingCount,
    DateTime? OldestPendingAt,
    int DeadLetterCount);

/// <summary>メールの送信待ちの読み書き。</summary>
public interface IMailOutbox
{
    /// <summary>1 通を送信待ちへ積む。</summary>
    /// <param name="mailId">識別子。**呼ぶ側が決める**（同じ通知を二重に積まないため）。</param>
    /// <param name="kind">種類（<c>Core/Mail/MailKind</c>）。</param>
    /// <param name="surveyId">アンケート。**紐づかないなら <see cref="Guid.Empty"/>。**</param>
    /// <param name="payloadProtected">**暗号化済み**の 1 通ぶん。</param>
    /// <param name="cancellationToken">中断。</param>
    /// <returns>積んだなら <c>true</c>。**既に同じ識別子があれば <c>false</c>。**</returns>
    Task<bool> EnqueueAsync(
        Guid mailId,
        int kind,
        Guid surveyId,
        string payloadProtected,
        CancellationToken cancellationToken = default);

    /// <summary>送るべき 1 件を確保する。無ければ <c>null</c>。</summary>
    Task<PendingMail?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default);

    /// <summary>送れたので消す。**宛先も本文も残さない。**</summary>
    Task CompleteAsync(Guid mailId, CancellationToken cancellationToken = default);

    /// <summary>失敗したので後で送り直す。</summary>
    Task RescheduleAsync(
        Guid mailId,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default);

    /// <summary>送り直しても通らないので分離する。**管理者へ通知すること。**</summary>
    Task DeadLetterAsync(Guid mailId, string error, CancellationToken cancellationToken = default);

    /// <summary>期限切れの確保を解放する。**ワーカーが落ちてもメールは失われない。**</summary>
    Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default);

    /// <summary>滞留の状況を読む。</summary>
    Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
/// <remarks>**<c>ResponseOutbox</c> と同じ書き方に揃えてある。**</remarks>
public sealed class MailOutbox(IDbConnectionFactory connectionFactory) : IMailOutbox
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<bool> EnqueueAsync(
        Guid mailId,
        int kind,
        Guid surveyId,
        string payloadProtected,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadProtected);

        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var affected = await connection.ExecuteAsync(Sql(
                "INSERT INTO [MailOutbox] "
                + "([MailId], [Kind], [SurveyId], [PayloadProtected], [Status], [RetryCount], "
                + " [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                + "VALUES (@MailId, @Kind, @SurveyId, @PayloadProtected, @PendingStatus, 0, "
                + "        @Now, @Now, @Now)",
                new
                {
                    MailId = mailId,
                    Kind = kind,
                    SurveyId = surveyId,
                    PayloadProtected = payloadProtected,
                    PendingStatus = (int)MailStatus.Pending,
                    Now = now,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return affected > 0;
        }
        catch (DbException)
        {
            // **主キーの衝突＝既に積んである。** 二重に送らない側へ倒す。
            // ⚠️ **影響行数では見分けられない**（MySQL は一致行数を返す）ので、
            // 衝突そのもので判定する
            return false;
        }
    }

    public async Task<PendingMail?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        // **確保ごとに一意な値を入れる**（回答側と同じ。過去の確保と一致させない）
        var owner = lockedBy.Length > 24 ? lockedBy[..24] : lockedBy;
        var lockId = $"{owner}:{Guid.NewGuid():N}";

        var parameters = new
        {
            SendingStatus = (int)MailStatus.Sending,
            PendingStatus = (int)MailStatus.Pending,
            LockedBy = lockId,
            LockedUntil = now.Add(lockDuration),
            Now = now,
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (SqlDialect.SupportsReturning(Provider))
        {
            return await connection.QueryFirstOrDefaultAsync<PendingMail>(Sql(
                SqlDialect.ClaimPendingMail(Provider),
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // MySQL は RETURNING が無いので、確保してから読み直す
        var affected = await connection.ExecuteAsync(Sql(
            SqlDialect.ClaimPendingMail(Provider),
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 0
            ? null
            : await connection.QueryFirstOrDefaultAsync<PendingMail>(Sql(
                SqlDialect.ReadClaimedMailForMySql,
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CompleteAsync(Guid mailId, CancellationToken cancellationToken = default)
    {
        // **送れたら消す。「念のため」残さない**（宛先と本文を持ち続けない）
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [MailOutbox] WHERE [MailId] = @MailId",
            new { MailId = mailId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RescheduleAsync(
        Guid mailId,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [MailOutbox] SET "
            + "  [Status] = @PendingStatus, "
            + "  [RetryCount] = [RetryCount] + 1, "
            + "  [NextAttemptAt] = @NextAttemptAt, "
            + "  [LastError] = @Error, "
            + "  [LockedBy] = NULL, [LockedUntil] = NULL, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [MailId] = @MailId",
            new
            {
                MailId = mailId,
                PendingStatus = (int)MailStatus.Pending,
                NextAttemptAt = DbTime.ForDb(nextAttemptAtUtc),
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        Guid mailId,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [MailOutbox] SET "
            + "  [Status] = @DeadLetterStatus, "
            + "  [LastError] = @Error, "
            + "  [LockedBy] = NULL, [LockedUntil] = NULL, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [MailId] = @MailId",
            new
            {
                MailId = mailId,
                DeadLetterStatus = (int)MailStatus.DeadLetter,
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [MailOutbox] SET "
            + "  [Status] = @PendingStatus, "
            + "  [LockedBy] = NULL, [LockedUntil] = NULL, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [Status] = @SendingStatus AND [LockedUntil] < @Now",
            new
            {
                PendingStatus = (int)MailStatus.Pending,
                SendingStatus = (int)MailStatus.Sending,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>集約の結果を受ける行。</summary>
    /// <remarks>
    /// **<c>COUNT</c> の型が 3 者で違う**（SQL Server は <c>int</c>、他は <c>bigint</c>）。
    /// **書き換え可能な属性で受けると Dapper が合わせてくれる**（<c>ResponseOutbox</c> と同じ）。
    /// </remarks>
    private sealed class MailStatusRow
    {
        public long PendingCount { get; set; }

        public DateTime? OldestPendingAt { get; set; }

        public long DeadLetterCount { get; set; }
    }

    public async Task<MailOutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstAsync<MailStatusRow>(Sql(
            "SELECT "
            + "  COUNT(CASE WHEN [Status] <> @DeadLetterStatus THEN 1 END) AS [PendingCount], "
            + "  MIN(CASE WHEN [Status] <> @DeadLetterStatus THEN [CreatedAt] END) AS [OldestPendingAt], "
            + "  COUNT(CASE WHEN [Status] = @DeadLetterStatus THEN 1 END) AS [DeadLetterCount] "
            + "FROM [MailOutbox]",
            new { DeadLetterStatus = (int)MailStatus.DeadLetter },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new MailOutboxStatus(
            (int)row.PendingCount,
            DbTime.AsUtc(row.OldestPendingAt),
            (int)row.DeadLetterCount);
    }

    /// <summary>失敗の理由は列の桁に収める。**宛先も本文も入れないこと。**</summary>
    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

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
