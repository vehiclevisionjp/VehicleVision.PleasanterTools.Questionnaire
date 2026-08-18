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
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<long?>(new CommandDefinition(
            $"SELECT {q("PleasanterReferenceId")} FROM {q("ResponseTokens")} "
            + $"WHERE {q("ResponseToken")} = @ResponseToken",
            new { ResponseToken = responseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        string responseToken,
        Guid surveyId,
        long? referenceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            SqlDialect.SaveResponseToken(connectionFactory.Provider),
            new
            {
                ResponseToken = responseToken,
                SurveyId = surveyId,
                ReferenceId = referenceId,
                Now = DateTime.UtcNow,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
