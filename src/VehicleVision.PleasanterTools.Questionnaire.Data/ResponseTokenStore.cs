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
    /// <returns>
    /// このとき作ったなら <c>true</c>。既にあったなら <c>false</c>。
    /// **「新しい回答か、前の回答の編集か」はここでしか分からない**
    /// （どちらも同じ入口を通る）。受付数を数える側がこれで判断する（Issue #53）。
    /// </returns>
    /// <remarks>
    /// 受付のたびに <c>ReferenceId</c> を <c>null</c> で上書きすると、
    /// **編集が <c>Update</c> ではなく <c>Create</c> になり二重登録になる。**
    /// </remarks>
    Task<bool> EnsureAsync(
        string responseToken,
        Guid surveyId,
        CancellationToken cancellationToken = default);

    /// <summary>そのアンケートが受け付けた回答の件数（Issue #53）。</summary>
    /// <remarks>
    /// <para>
    /// **数えるのはこの表。** 送信待ち（<c>Responses</c>）は**送信できたら消える**ので、
    /// 数えると届いた分だけ件数が減っていく。デッドレターだけを数えても足りない。
    /// **回答者には受付完了と伝えている**以上、まだ Pleasanter へ届いていない回答も
    /// 「受け付けた」に含めるのが素直（<c>_documents/画面設計.md</c> 1 章）。
    /// </para>
    /// <para>
    /// **回答 1 件につき 1 行**（トークンが主キー）。回答を編集しても同じトークンを使うので、
    /// **編集で件数が増えることはない。**
    /// </para>
    /// <para>
    /// **1 回の問い合わせで済ませる。** <c>IX_ResponseTokens_SurveyId</c> があるので
    /// 表の全体は走らない。**上限を設けたアンケートは上限に達した時点で自動停止する**ので、
    /// 数える対象の行数は上限の大きさで頭打ちになる。
    /// </para>
    /// </remarks>
    Task<int> CountAcceptedAsync(
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

    public async Task<bool> EnsureAsync(
        string responseToken,
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseToken);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **作ったかどうかを「影響した行数」で判断しない。**
        // MySQL は既定で「一致した行数」を返すので、3 者で数え方が割れる
        // （実際に MySQL でだけ落ちた）。**主キーの衝突で見分ける。**
        try
        {
            await connection.ExecuteAsync(Sql(
                SqlDialect.InsertResponseToken,
                new { ResponseToken = responseToken, SurveyId = surveyId, Now = DbTime.ForDb(DateTime.Now) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return true;
        }
        catch (DbException)
        {
            // **例外の番号で判断しない**（3 者で違う）。
            // **行が在ることを確かめて、在れば「既にあった」。**
            // 在らなければ本当の失敗なので、そのまま投げ直す
            var found = await connection.ExecuteScalarAsync<int?>(Sql(
                "SELECT 1 FROM [ResponseTokens] WHERE [ResponseToken] = @ResponseToken",
                new { ResponseToken = responseToken },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (found is null)
            {
                throw;
            }

            return false;
        }
    }

    public async Task<int> CountAcceptedAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **COUNT の型は 3 者で違う**（SQL Server は int、他は bigint）が、
        // 単独の値として取る分には Dapper が合わせてくれる
        return await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [ResponseTokens] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
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
