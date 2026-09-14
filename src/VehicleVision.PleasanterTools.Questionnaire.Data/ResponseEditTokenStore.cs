using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>再編集リンクのトークンの読み書き（Issue #202）。</summary>
/// <remarks>
/// ⚠️ **扱うのはハッシュだけ。** 元のトークンはここへ渡さない
/// （渡す側が潰してから呼ぶ。<c>Web/Services/ResponseEditLink</c>）。
/// </remarks>
public interface IResponseEditTokenStore
{
    /// <summary>1 本発行する。</summary>
    /// <param name="editTokenHash">トークンのハッシュ。</param>
    /// <param name="responseToken">書き換えを許す回答。</param>
    /// <param name="surveyId">アンケート。**一括失効に要る。**</param>
    /// <param name="expiresAtUtc">期限（UTC）。</param>
    /// <param name="cancellationToken">中断。</param>
    Task SaveAsync(
        string editTokenHash,
        string responseToken,
        Guid surveyId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>引き換える。**使えなければ <c>null</c>。**</summary>
    /// <remarks>
    /// <para>
    /// **消さない。期限内は何度でも使える**（2026-09-14 決定）。
    /// </para>
    /// <para>
    /// ⚠️ **理由を区別して返さない。** 期限切れ・失効済み・そんなトークンは無い、を
    /// 呼ぶ側が見分けられると、**総当たりで「実在するか」が分かってしまう。**
    /// </para>
    /// </remarks>
    /// <returns>書き換えを許す回答のトークン。</returns>
    Task<string?> RedeemAsync(
        string editTokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>そのアンケートのリンクを全部失効させる。**漏れたときの止め方。**</summary>
    /// <returns>失効させた件数。</returns>
    Task<int> RevokeBySurveyAsync(Guid surveyId, CancellationToken cancellationToken = default);

    /// <summary>期限を過ぎた行を消す。**掃除。**</summary>
    /// <returns>消した件数。</returns>
    Task<int> DeleteExpiredAsync(DateTime threshold, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class ResponseEditTokenStore(IDbConnectionFactory connectionFactory)
    : IResponseEditTokenStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task SaveAsync(
        string editTokenHash,
        string responseToken,
        Guid surveyId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editTokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseToken);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [ResponseEditTokens] "
            + "([EditTokenHash], [ResponseToken], [SurveyId], [ExpiresAt], [CreatedAt]) "
            + "VALUES (@EditTokenHash, @ResponseToken, @SurveyId, @ExpiresAt, @Now)",
            new
            {
                EditTokenHash = editTokenHash,
                ResponseToken = responseToken,
                SurveyId = surveyId,
                ExpiresAt = DbTime.ForDb(expiresAtUtc),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>引き換えで読む行。</summary>
    /// <remarks>
    /// **単独の値ではなく行として読む**（<c>ResponseOutbox</c> と同じ理由。
    /// MySQL の型変換に引っ掛からないようにする）。
    /// </remarks>
    private sealed record RedeemedRow(string ResponseToken);

    public async Task<string?> RedeemAsync(
        string editTokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(editTokenHash))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **消さない。** 期限内は何度でも使える
        var row = await connection.QueryFirstOrDefaultAsync<RedeemedRow>(Sql(
            "SELECT [ResponseToken] FROM [ResponseEditTokens] "
            + "WHERE [EditTokenHash] = @EditTokenHash AND [ExpiresAt] > @Now",
            new { EditTokenHash = editTokenHash, Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row?.ResponseToken;
    }

    public async Task<int> RevokeBySurveyAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        // **消してしまう。** 失効の旗を立てるより、行が無いことを「使えない」にする方が短い
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [ResponseEditTokens] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [ResponseEditTokens] WHERE [ExpiresAt] < @Threshold",
            new { Threshold = DbTime.ForDb(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

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
