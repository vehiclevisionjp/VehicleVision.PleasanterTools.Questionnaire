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
        await connection.ExecuteAsync(new CommandDefinition(
            SqlDialect.SaveResponse(Provider),
            new
            {
                ResponseToken = responseToken,
                SurveyId = surveyId,
                SurveyVersion = surveyVersion,
                PayloadJson = payloadJson,
                PendingStatus = (int)ResponseStatus.Pending,
                Now = UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PendingResponse?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNowTruncated();

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
            return await connection.QueryFirstOrDefaultAsync<PendingResponse>(new CommandDefinition(
                SqlDialect.ClaimPendingResponse(Provider),
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // MySQL は RETURNING が無いので、確保してから読み直す
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            SqlDialect.ClaimPendingResponse(Provider),
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 0
            ? null
            : await connection.QueryFirstOrDefaultAsync<PendingResponse>(new CommandDefinition(
                SqlDialect.ReadClaimedResponseForMySql,
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CompleteAsync(string responseToken, CancellationToken cancellationToken = default)
    {
        // **送信できたら消す。「念のため」残さない**（個人情報を含み得るため）
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM {Q("Responses")} WHERE {Q("ResponseToken")} = @ResponseToken",
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
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("Responses")} SET " +
            $"  {Q("Status")} = @PendingStatus, " +
            $"  {Q("RetryCount")} = {Q("RetryCount")} + 1, " +
            $"  {Q("NextAttemptAt")} = @NextAttemptAt, " +
            $"  {Q("LastError")} = @Error, " +
            $"  {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL, " +
            $"  {Q("UpdatedAt")} = @Now " +
            $"WHERE {Q("ResponseToken")} = @ResponseToken",
            new
            {
                ResponseToken = responseToken,
                PendingStatus = (int)ResponseStatus.Pending,
                NextAttemptAt = nextAttemptAtUtc,
                Error = Truncate(error),
                Now = UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        string responseToken,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("Responses")} SET " +
            $"  {Q("Status")} = @DeadLetterStatus, " +
            $"  {Q("LastError")} = @Error, " +
            $"  {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL, " +
            $"  {Q("UpdatedAt")} = @Now " +
            $"WHERE {Q("ResponseToken")} = @ResponseToken",
            new
            {
                ResponseToken = responseToken,
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
                Error = Truncate(error),
                Now = UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default)
    {
        // **確保中に落ちたら未送信へ戻る。** 回答を失わないための仕掛け
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {Q("Responses")} SET " +
            $"  {Q("Status")} = @PendingStatus, " +
            $"  {Q("LockedBy")} = NULL, {Q("LockedUntil")} = NULL, " +
            $"  {Q("UpdatedAt")} = @Now " +
            $"WHERE {Q("Status")} = @SendingStatus AND {Q("LockedUntil")} < @Now",
            new
            {
                PendingStatus = (int)ResponseStatus.Pending,
                SendingStatus = (int)ResponseStatus.Sending,
                Now = UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<string?> FindPayloadAsync(
        string responseToken,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(
            $"SELECT {Q("PayloadJson")} FROM {Q("Responses")} "
            + $"WHERE {Q("ResponseToken")} = @ResponseToken",
            new { ResponseToken = responseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountPendingAsync(
        Guid? surveyId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = surveyId is null ? string.Empty : $" AND {Q("SurveyId")} = @SurveyId";

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {Q("Responses")} WHERE {Q("Status")} <> @DeadLetterStatus{filter}",
            new { DeadLetterStatus = (int)ResponseStatus.DeadLetter, SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private string Q(string identifier) => SqlDialect.Quote(Provider, identifier);

    /// <summary>秒未満を切り捨てた現在時刻（UTC）。</summary>
    /// <remarks>
    /// **MySQL の <c>datetime</c> は秒未満を保持せず、四捨五入して格納する**
    /// （<c>_documents/データモデル設計.md</c> 4 章）。
    /// 切り捨てずに渡すと、保存直後の <c>NextAttemptAt</c> が**現在より未来に丸められ**、
    /// その回答が次の秒まで確保できなくなる（実際に MySQL で踏んだ）。
    /// **3 者で同じ振る舞いにするため、アプリ側で切り捨ててから渡す。**
    /// </remarks>
    private static DateTime UtcNowTruncated()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>失敗の理由は列の桁に収める。**回答本文は入れないこと。**</summary>
    private static string? Truncate(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
