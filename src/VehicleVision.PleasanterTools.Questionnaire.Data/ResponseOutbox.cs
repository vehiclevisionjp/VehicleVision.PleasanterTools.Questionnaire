using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>送信待ちの状態。</summary>
/// <remarks>
/// **<c>Sent</c> は持たない。送信できたら行を消す**
/// （<c>_documents/データモデル設計.md</c> 2.5）。
/// </remarks>
public enum ResponseStatus
{
    Pending = 0,
    Sending = 1,
    DeadLetter = 2,
}

/// <summary>送信待ちの 1 件。</summary>
public sealed record PendingResponse(
    string ResponseToken,
    Guid SurveyId,
    int SurveyVersion,
    string PayloadJson,
    int RetryCount);

/// <summary>送信待ちテーブルの読み書き。</summary>
public interface IResponseOutbox
{
    /// <summary>回答を保存する。**同じトークンなら上書きする。**</summary>
    Task SaveAsync(
        string responseToken,
        Guid surveyId,
        int surveyVersion,
        string payloadJson,
        CancellationToken cancellationToken = default);

    /// <summary>送るべき 1 件を確保する。無ければ <c>null</c>。</summary>
    Task<PendingResponse?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default);

    /// <summary>送信できたので消す。</summary>
    Task CompleteAsync(string responseToken, CancellationToken cancellationToken = default);

    /// <summary>失敗したので後で再送する。</summary>
    Task RescheduleAsync(
        string responseToken,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default);

    /// <summary>再送しても通らないので分離する。**管理者へ通知すること。**</summary>
    Task DeadLetterAsync(
        string responseToken,
        string error,
        CancellationToken cancellationToken = default);

    /// <summary>期限切れの確保を解放する。**ワーカーが落ちても回答は失われない。**</summary>
    Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default);

    /// <summary>未送信の回答を読む。**送信待ちを先に見るため**（読み出しの 2 段構え）。</summary>
    Task<string?> FindPayloadAsync(string responseToken, CancellationToken cancellationToken = default);

    /// <summary>未送信の件数。**溜まっていることに気づけるようにする。**</summary>
    Task<int> CountPendingAsync(Guid? surveyId = null, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class ResponseOutbox(IDbConnectionFactory connectionFactory) : IResponseOutbox
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task SaveAsync(
        string responseToken,
        Guid surveyId,
        int surveyVersion,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            SqlDialect.SaveResponse(Provider),
            new
            {
                ResponseToken = responseToken,
                SurveyId = surveyId,
                SurveyVersion = surveyVersion,
                PayloadJson = payloadJson,
                PendingStatus = (int)ResponseStatus.Pending,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PendingResponse?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        // **確保ごとに一意な値を入れる。** MySQL は RETURNING が無く、確保した行を
        // 読み直す必要がある。呼び出し元の名前だけだと**過去の確保とも一致してしまい、
        // 今確保した行を特定できない**（実際に MySQL で踏んだ）
        var owner = lockedBy.Length > 24 ? lockedBy[..24] : lockedBy;
        var lockId = $"{owner}:{Guid.NewGuid():N}";

        var parameters = new
        {
            SendingStatus = (int)ResponseStatus.Sending,
            PendingStatus = (int)ResponseStatus.Pending,
            LockedBy = lockId,
            LockedUntil = now.Add(lockDuration),
            Now = now,
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (SqlDialect.SupportsReturning(Provider))
        {
            return await connection.QueryFirstOrDefaultAsync<PendingResponse>(Sql(
                SqlDialect.ClaimPendingResponse(Provider),
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // MySQL は RETURNING が無いので、確保してから読み直す
        var affected = await connection.ExecuteAsync(Sql(
            SqlDialect.ClaimPendingResponse(Provider),
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 0
            ? null
            : await connection.QueryFirstOrDefaultAsync<PendingResponse>(Sql(
                SqlDialect.ReadClaimedResponseForMySql,
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string responseToken, CancellationToken cancellationToken = default)
    {
        // **送信できたら消す。「念のため」残さない**（個人情報を含み得るため）
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [Responses] WHERE [ResponseToken] = @ResponseToken",
            new { ResponseToken = responseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RescheduleAsync(
        string responseToken,
        DateTime nextAttemptAtUtc,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [Responses] SET " +
            "  [Status] = @PendingStatus, " +
            "  [RetryCount] = [RetryCount] + 1, " +
            "  [NextAttemptAt] = @NextAttemptAt, " +
            "  [LastError] = @Error, " +
            "  [LockedBy] = NULL, [LockedUntil] = NULL, " +
            "  [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken",
            new
            {
                ResponseToken = responseToken,
                PendingStatus = (int)ResponseStatus.Pending,
                NextAttemptAt = DbTime.ForDb(nextAttemptAtUtc),
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        string responseToken,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [Responses] SET " +
            "  [Status] = @DeadLetterStatus, " +
            "  [LastError] = @Error, " +
            "  [LockedBy] = NULL, [LockedUntil] = NULL, " +
            "  [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken",
            new
            {
                ResponseToken = responseToken,
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default)
    {
        // **確保中に落ちたら未送信へ戻る。** 回答を失わないための仕掛け
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [Responses] SET " +
            "  [Status] = @PendingStatus, " +
            "  [LockedBy] = NULL, [LockedUntil] = NULL, " +
            "  [UpdatedAt] = @Now " +
            "WHERE [Status] = @SendingStatus AND [LockedUntil] < @Now",
            new
            {
                PendingStatus = (int)ResponseStatus.Pending,
                SendingStatus = (int)ResponseStatus.Sending,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<string?> FindPayloadAsync(
        string responseToken,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<string>(Sql(
            "SELECT [PayloadJson] FROM [Responses] "
            + "WHERE [ResponseToken] = @ResponseToken",
            new { ResponseToken = responseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountPendingAsync(
        Guid? surveyId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = surveyId is null ? string.Empty : " AND [SurveyId] = @SurveyId";

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Sql(
            $"SELECT COUNT(*) FROM [Responses] WHERE [Status] <> @DeadLetterStatus{filter}",
            new { DeadLetterStatus = (int)ResponseStatus.DeadLetter, SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }



    /// <summary>失敗の理由は列の桁に収める。**回答本文は入れないこと。**</summary>
    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

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
