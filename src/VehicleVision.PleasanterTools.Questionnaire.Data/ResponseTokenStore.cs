using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>回答トークンと Pleasanter の <c>ReferenceId</c> の対応。</summary>
/// <remarks>
/// <para>
/// **Pleasanter の列を使わずに回答を突き止めるための表**
/// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
/// 列は型ごとに 26 本しか無いが、本アプリの DB の行は安い。
/// </para>
/// <para>
/// **送信待ちが消えても、この表は残す。** 回答の編集で <c>ReferenceId</c> を引くために要る。
/// </para>
/// </remarks>
public interface IResponseTokenStore
{
    /// <summary>トークンに対応する <c>ReferenceId</c> を返す。まだ作っていなければ <c>null</c>。</summary>
    Task<long?> FindReferenceIdAsync(
        string responseToken,
        CancellationToken cancellationToken = default);

    /// <summary>行が無ければ作る。**既にある <c>ReferenceId</c> は触らない。**</summary>
    /// <remarks>
    /// 受付のたびに <c>ReferenceId</c> を <c>null</c> で上書きすると、
    /// **編集が <c>Update</c> ではなく <c>Create</c> になり二重登録になる。**
    /// </remarks>
    Task EnsureAsync(
        string responseToken,
        Guid surveyId,
        CancellationToken cancellationToken = default);

    /// <summary>対応を保存する。<paramref name="referenceId"/> が <c>null</c> なら未作成のまま記録する。</summary>
    Task SaveAsync(
        string responseToken,
        Guid surveyId,
        long? referenceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class ResponseTokenStore(IDbConnectionFactory connectionFactory) : IResponseTokenStore
{
    public async Task<long?> FindReferenceIdAsync(
        string responseToken,
        CancellationToken cancellationToken = default)
    {

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<long?>(Sql(
            "SELECT [PleasanterReferenceId] FROM [ResponseTokens] "
            + "WHERE [ResponseToken] = @ResponseToken",
            new { ResponseToken = responseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task EnsureAsync(
        string responseToken,
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            SqlDialect.EnsureResponseToken(connectionFactory.Provider),
            new { ResponseToken = responseToken, SurveyId = surveyId, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        string responseToken,
        Guid surveyId,
        long? referenceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            SqlDialect.SaveResponseToken(connectionFactory.Provider),
            new
            {
                ResponseToken = responseToken,
                SurveyId = surveyId,
                ReferenceId = referenceId,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private DatabaseProvider Provider => connectionFactory.Provider;

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
