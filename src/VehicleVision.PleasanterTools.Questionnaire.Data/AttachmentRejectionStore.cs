using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>添付を弾いた記録 1 件（Issue #39）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **送信元も回答本文もファイル名も持たない。**
/// 回答者は完全匿名という前提があり（<c>_documents/アーキテクチャ方針.md</c>）、
/// **ファイル名には氏名が入り得る**（「履歴書_山田太郎.pdf」）。
/// **型に置き場所を作らないことで、後から足される余地を消している。**
/// </para>
/// <para>
/// **1 回の送信につき、理由ごとに 1 行。** 1 件ずつ入れると、
/// 添付を並べて送るだけで行を好きなだけ増やせる。
/// </para>
/// </remarks>
/// <param name="OccurredAt">弾いた時刻（UTC）。</param>
/// <param name="SurveyId">どのアンケートで起きたか。**設定を直すのはアンケート単位。**</param>
/// <param name="QuestionId">
/// どの設問か。**設問に紐づかない理由（合計サイズ超過など）では <c>null</c>。**
/// </param>
/// <param name="Reason">
/// 弾いた理由（<c>Core/Attachments/AttachmentRejectionReason</c> の値）。
/// **`.Data` は `.Core` を参照しないので、ここでは整数で持つ。**
/// </param>
/// <param name="FileCount">その送信で、その理由に当たった件数。</param>
public sealed record AttachmentRejectionEntry(
    DateTime OccurredAt,
    Guid SurveyId,
    string? QuestionId,
    int Reason,
    int FileCount);

/// <summary>画面に出す、弾いた記録 1 件。</summary>
/// <param name="SurveyTitle">アンケートの題名。**消えたアンケートでは <c>null</c>。**</param>
public sealed record AttachmentRejectionView(
    DateTime OccurredAt,
    Guid SurveyId,
    string? SurveyTitle,
    string? QuestionId,
    int Reason,
    int FileCount);

/// <summary>記録を読むときの絞り込み。</summary>
public sealed record AttachmentRejectionQuery
{
    /// <summary>読む件数の上限。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>読み飛ばす件数。**ページ送り用。**</summary>
    public int Offset { get; init; }
}

/// <summary>添付を弾いた記録を読み書きする（Issue #39）。</summary>
/// <remarks>
/// <para>
/// **管理操作の記録（<c>IAuditLogStore</c>）と混ぜない。**
/// あちらは <c>IpAddress</c> を持つ表で、**これは回答者側の出来事**。
/// 同じ表へ入れると、完全匿名という前提と静かに衝突する。
/// </para>
/// <para>
/// **書けなくても回答の処理を止めないこと。** 記録は運用のためのもので、
/// これが失敗したせいで受付の結果が変わってはいけない（呼ぶ側の責任）。
/// </para>
/// </remarks>
public interface IAttachmentRejectionStore
{
    /// <summary>弾いた記録をまとめて書く。**理由ごとに 1 行。**</summary>
    Task WriteAsync(
        IReadOnlyCollection<AttachmentRejectionEntry> entries,
        CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AttachmentRejectionView>> ListAsync(
        AttachmentRejectionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>直近の件数。**「今どれくらい弾いているか」を数字 1 つで見るため。**</summary>
    Task<int> CountSinceAsync(DateTime since, CancellationToken cancellationToken = default);

    /// <summary>指定した時刻より古い記録を消す。**消さないと増え続ける。**</summary>
    /// <returns>消した件数。</returns>
    Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class AttachmentRejectionStore(IDbConnectionFactory connectionFactory)
    : IAttachmentRejectionStore
{
    /// <summary>列の桁。**溢れさせずに切り詰める。**</summary>
    /// <remarks>**記録できなかったより、切り詰めた方がまし。**</remarks>
    private const int QuestionIdLength = 128;

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task WriteAsync(
        IReadOnlyCollection<AttachmentRejectionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **Dapper はコレクションを渡すと 1 件ずつ実行する。**
        // まとめて書きたいだけなので、方言ごとの複数行 INSERT は用意しない
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AttachmentRejections] "
            + "([AttachmentRejectionId], [OccurredAt], [SurveyId], "
            + " [QuestionId], [Reason], [FileCount]) "
            + "VALUES (@AttachmentRejectionId, @OccurredAt, @SurveyId, "
            + "        @QuestionId, @Reason, @FileCount)",
            entries.Select(entry => new
            {
                AttachmentRejectionId = Guid.NewGuid(),
                OccurredAt = DbTime.ForDb(entry.OccurredAt),
                entry.SurveyId,
                QuestionId = Trim(entry.QuestionId, QuestionIdLength),
                entry.Reason,
                entry.FileCount,
            }).ToArray(),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AttachmentRejectionView>> ListAsync(
        AttachmentRejectionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AttachmentRejectionView>(Sql(
            SqlDialect.ListAttachmentRejections(Provider),
            new { Limit = query.Limit, Offset = query.Offset },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<int> CountSinceAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **行数ではなく弾いた件数を数える。** 1 行が複数件を表すため
        return await connection.ExecuteScalarAsync<int?>(Sql(
            "SELECT SUM([FileCount]) FROM [AttachmentRejections] WHERE [OccurredAt] >= @Since",
            new { Since = DbTime.ForDb(since) },
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;
    }

    public async Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AttachmentRejections] WHERE [OccurredAt] < @Threshold",
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
