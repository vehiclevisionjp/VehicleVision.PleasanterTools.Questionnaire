using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>画面に出す知らせ 1 件（Issue #80）。</summary>
/// <remarks>
/// ⚠️ **回答本文も資格情報も <c>ResponseToken</c> も送信元も持たない。**
/// 監査ログと同じ決まりを、注意書きではなく型で守る。
/// </remarks>
/// <param name="Kind">
/// 種類（<c>Core/Notifications/AdminNotificationKind</c> の値）。
/// **<c>.Data</c> は <c>.Core</c> を参照しないので、ここでは整数で持つ。**
/// </param>
/// <param name="SurveyId">
/// どのアンケートの話か。**紐づかない知らせでは <see cref="Guid.Empty"/>。**
/// </param>
/// <param name="SurveyTitle">アンケートの題名。**消えたアンケートでは <c>null</c>。**</param>
/// <param name="Count">同じ知らせが起きた回数。</param>
/// <param name="FirstOccurredAt">最初に起きた時刻（UTC）。</param>
/// <param name="LastOccurredAt">最後に起きた時刻（UTC）。</param>
/// <param name="ReadAt">既読にした時刻（UTC）。**未読なら <c>null</c>。**</param>
public sealed record AdminNotificationView(
    Guid AdminNotificationId,
    int Kind,
    Guid SurveyId,
    string? SurveyTitle,
    int Count,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt,
    DateTime? ReadAt);

/// <summary>回答通知メールへ積む対象。</summary>
/// <remarks>⚠️ **回答本文を持たない。** 件数・時刻・アンケート名だけを渡す。</remarks>
public sealed record ResponseNotificationDigest(
    Guid SurveyId,
    string? SurveyTitle,
    int Count,
    int MailQueuedCount,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt,
    DateTime? LastMailQueuedAt);

/// <summary>暗号化済みの回答通知メール。</summary>
public sealed record ProtectedResponseNotificationMail(Guid MailId, string PayloadProtected);

/// <summary>知らせを読むときの絞り込み。</summary>
public sealed record AdminNotificationQuery
{
    /// <summary>読む件数の上限。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>読み飛ばす件数。**ページ送り用。**</summary>
    public int Offset { get; init; }

    /// <summary>未読だけを読むか。</summary>
    public bool UnreadOnly { get; init; }
}

/// <summary>管理者への知らせを読み書きする（Issue #80）。</summary>
/// <remarks>
/// <para>
/// **書けなくても本来の処理を止めないこと。** 知らせは気付くためのもので、
/// これが失敗したせいで回答の受付や送信の結果が変わってはいけない（呼ぶ側の責任）。
/// </para>
/// <para>
/// **既読は全体で 1 つ。** 誰かが既読にしたら全員にとって既読
/// （<c>_documents/アーキテクチャ方針.md</c> 15 章の決定 5）。
/// </para>
/// </remarks>
public interface IAdminNotificationStore
{
    /// <summary>知らせを 1 件立てる。**未読の同じ種類があれば件数と時刻を足す。**</summary>
    /// <remarks>
    /// ⚠️ **1 件ずつ行を作らない。** Pleasanter が壊れていれば回答は全部デッドレターになる。
    /// </remarks>
    Task RaiseAsync(
        int kind,
        Guid surveyId,
        DateTime occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AdminNotificationView>> ListAsync(
        AdminNotificationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>未読の件数。**行数ではなく起きた回数を数える。**</summary>
    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);

    /// <summary>未読をすべて既読にする。</summary>
    /// <returns>既読にした行数。</returns>
    Task<int> MarkAllReadAsync(DateTime readAt, CancellationToken cancellationToken = default);

    /// <summary>指定した時刻より古い知らせを消す。**消さないと増え続ける。**</summary>
    /// <remarks>**未読は消さない。** 誰も見ていない知らせを黙って捨てない。</remarks>
    /// <returns>消した件数。</returns>
    Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default);
}

/// <summary>回答通知メールの集約状態を読み、メール送信待ちへ確定する。</summary>
public interface IResponseNotificationStore
{
    /// <summary>初回受付または前回送信から指定時刻まで経過した、未送信分を読む。</summary>
    Task<IReadOnlyList<ResponseNotificationDigest>> ListDueResponseDigestsAsync(
        DateTime dueBefore,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 暗号化済みメールを積み、通知の送信済み件数を進める。
    /// **同じ通知を別のワーカーが先に処理していたら <c>false</c>。**
    /// </summary>
    Task<bool> TryQueueResponseDigestAsync(
        ResponseNotificationDigest digest,
        IReadOnlyList<ProtectedResponseNotificationMail> mails,
        DateTime queuedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class AdminNotificationStore(IDbConnectionFactory connectionFactory)
    : IAdminNotificationStore, IResponseNotificationStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task RaiseAsync(
        int kind,
        Guid surveyId,
        DateTime occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new
        {
            Kind = kind,
            SurveyId = surveyId,
            OccurredAt = DbTime.ForDb(occurredAt),
        };

        // **1 文の UPDATE で足し、0 行なら INSERT。**
        // 更新で必ず値が変わる（Count が増える）ので、
        // 「一致行数」を返す MySQL でも判定が割れない
        var updated = await connection.ExecuteAsync(Sql(
            "UPDATE [AdminNotifications] "
            + "SET [Count] = [Count] + 1, [LastOccurredAt] = @OccurredAt "
            + "WHERE [Kind] = @Kind AND [SurveyId] = @SurveyId AND [ReadAt] IS NULL",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (updated == 0)
        {
            // **同時に立った知らせが 2 行になり得る。** 未読が 2 行あっても、
            // 未読の件数は合計で数えるので画面の意味は変わらない。
            // ここを一意制約で守ろうとすると「未読だけ一意」が要り、3 RDBMS で書き方が割れる
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [AdminNotifications] "
                + "([AdminNotificationId], [Kind], [SurveyId], [Count], "
                + " [FirstOccurredAt], [LastOccurredAt], [ReadAt]) "
                + "VALUES (@AdminNotificationId, @Kind, @SurveyId, 1, "
                + "        @OccurredAt, @OccurredAt, NULL)",
                new
                {
                    AdminNotificationId = Guid.NewGuid(),
                    parameters.Kind,
                    parameters.SurveyId,
                    parameters.OccurredAt,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (kind == 7)
        {
            await AccumulateResponseDigestAsync(
                connection, surveyId, occurredAt, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<AdminNotificationView>> ListAsync(
        AdminNotificationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<AdminNotificationView>(Sql(
            SqlDialect.ListAdminNotifications(Provider, query.UnreadOnly),
            new { Limit = query.Limit, Offset = query.Offset },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **行数ではなく起きた回数。** 1 行が「デッドレター 300 件」を表すことがある
        return await connection.ExecuteScalarAsync<int?>(Sql(
            "SELECT SUM([Count]) FROM [AdminNotifications] WHERE [ReadAt] IS NULL",
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;
    }

    public async Task<int> MarkAllReadAsync(
        DateTime readAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(Sql(
            "UPDATE [AdminNotifications] SET [ReadAt] = @ReadAt WHERE [ReadAt] IS NULL",
            new { ReadAt = DbTime.ForDb(readAt) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **未読は残す。** 気付く前に消えたら、溜める意味が無い
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AdminNotifications] "
            + "WHERE [LastOccurredAt] < @Threshold AND [ReadAt] IS NOT NULL",
            new { Threshold = DbTime.ForDb(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResponseNotificationDigest>> ListDueResponseDigestsAsync(
        DateTime dueBefore,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ResponseNotificationDigest>(Sql(
            "SELECT d.[SurveyId], s.[Title] AS [SurveyTitle], "
            + "       d.[Count], d.[MailQueuedCount], d.[FirstOccurredAt], d.[LastOccurredAt], "
            + "       d.[LastMailQueuedAt] "
            + "FROM [ResponseNotificationDigests] d "
            + "LEFT JOIN [Surveys] s ON s.[SurveyId] = d.[SurveyId] "
            + "WHERE d.[Count] > d.[MailQueuedCount] "
            + "  AND COALESCE(d.[LastMailQueuedAt], d.[FirstOccurredAt]) <= @DueBefore "
            + "ORDER BY d.[LastOccurredAt], d.[SurveyId] "
            + SqlDialect.Page(Provider),
            new
            {
                DueBefore = DbTime.ForDb(dueBefore),
                Limit = 100,
                Offset = 0,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<bool> TryQueueResponseDigestAsync(
        ResponseNotificationDigest digest,
        IReadOnlyList<ProtectedResponseNotificationMail> mails,
        DateTime queuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(mails);

        var now = DbTime.ForDb(queuedAt);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **先に件数を確保する。** 同じ集約を見た複数ワーカーのうち 1 つだけが進める。
        var claimed = await connection.ExecuteAsync(Sql(
            "UPDATE [ResponseNotificationDigests] "
            + "SET [MailQueuedCount] = @Count, [LastMailQueuedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId "
            + "  AND [MailQueuedCount] = @PreviousCount",
            new
            {
                digest.SurveyId,
                digest.Count,
                PreviousCount = digest.MailQueuedCount,
                Now = now,
            },
            transaction,
            cancellationToken)).ConfigureAwait(false);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var mail in mails)
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [MailOutbox] "
                + "([MailId], [Kind], [SurveyId], [PayloadProtected], [Status], [RetryCount], "
                + " [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                + "VALUES (@MailId, @Kind, @SurveyId, @PayloadProtected, 0, 0, @Now, @Now, @Now)",
                new
                {
                    mail.MailId,
                    Kind = 4,
                    digest.SurveyId,
                    mail.PayloadProtected,
                    Now = now,
                },
                transaction,
                cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task AccumulateResponseDigestAsync(
        DbConnection connection,
        Guid surveyId,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var parameters = new
        {
            SurveyId = surveyId,
            OccurredAt = DbTime.ForDb(occurredAt),
        };
        var updated = await connection.ExecuteAsync(Sql(
            "UPDATE [ResponseNotificationDigests] "
            + "SET [Count] = [Count] + 1, [LastOccurredAt] = @OccurredAt "
            + "WHERE [SurveyId] = @SurveyId",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (updated > 0)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [ResponseNotificationDigests] "
                + "([SurveyId], [Count], [MailQueuedCount], [FirstOccurredAt], "
                + " [LastOccurredAt], [LastMailQueuedAt]) "
                + "VALUES (@SurveyId, 1, 0, @OccurredAt, @OccurredAt, NULL)",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // **同時に最初の回答が来て主キーが衝突した。** 後から来た側も数え落とさない
            await connection.ExecuteAsync(Sql(
                "UPDATE [ResponseNotificationDigests] "
                + "SET [Count] = [Count] + 1, [LastOccurredAt] = @OccurredAt "
                + "WHERE [SurveyId] = @SurveyId",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql),
            parameters,
            transaction,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
