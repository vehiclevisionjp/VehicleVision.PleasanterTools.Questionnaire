using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>使い終えた proof-of-work の課題を覚える。</summary>
/// <remarks>
/// <para>
/// **同じ解答を 2 度通さないために持つ**（Issue #55）。
/// 記録しないと、**1 回解くだけで何度でも投稿できる**。
/// </para>
/// <para>
/// **DB に置く。** メモリに持つと、スケールアウトで別のインスタンスが同じ解答を通し、
/// 再起動で忘れる。**忘れた分だけ使い回しが通る。**
/// </para>
/// <para>
/// **回答者を識別しない。** 入るのは課題の文字列と期限だけで、
/// IP も回答トークンも入れない（完全匿名）。
/// </para>
/// </remarks>
public interface IAltchaChallengeStore
{
    /// <summary>使い終えたものとして覚える。</summary>
    Task StoreAsync(string challenge, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <summary>もう使われているか。</summary>
    Task<bool> ExistsAsync(string challenge, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class AltchaChallengeStore(IDbConnectionFactory connectionFactory) : IAltchaChallengeStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task StoreAsync(
        string challenge,
        DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **同じ課題が二重に入っても落とさない。** 先に入っていること自体が
        // 「もう使われている」の答えなので、書き込みの競合で送信を落とす必要はない
        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [AltchaChallenges] ([Challenge], [ExpiresAt]) "
                + "VALUES (@Challenge, @ExpiresAt)",
                new { Challenge = challenge, ExpiresAt = DbTime.ForDb(expiresAt) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 主キーの重複。**既に使われている**ということなので、これでよい
        }
    }

    public async Task<bool> ExistsAsync(
        string challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **期限切れは無かったことにする。** 課題そのものに期限があるので、
        // 過ぎたものを覚えておく意味は無い（放っておくと増え続ける）
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [AltchaChallenges] WHERE [ExpiresAt] < @Now",
            new { Now = DbTime.ForDb(DateTime.Now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var found = await connection.ExecuteScalarAsync<int?>(Sql(
            "SELECT 1 FROM [AltchaChallenges] WHERE [Challenge] = @Challenge",
            new { Challenge = challenge },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return found is not null;
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
