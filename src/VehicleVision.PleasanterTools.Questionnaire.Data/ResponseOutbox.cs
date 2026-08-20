using System.Collections.Immutable;
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

/// <summary>送信の滞留の状況。**管理画面の「送信状況」に出す数字。**</summary>
/// <remarks>
/// <para>
/// **回答本文は持たない。** 個人情報が入り得るので、画面へ出すのは
/// 件数と滞留の時刻だけにする（<c>_documents/データモデル設計.md</c> 2.5）。
/// </para>
/// <para>
/// **時刻は UTC**（列が時間帯を持たないので <see cref="DateTimeKind.Unspecified"/> で返る）。
/// 画面へ返す前に <see cref="DbTime.AsUtc(DateTime?)"/> を通すこと。
/// </para>
/// </remarks>
/// <param name="PendingCount">送信待ちの件数。**デッドレターは数えない。**</param>
/// <param name="OldestPendingAt">
/// 送信待ちのうち、最も古い受付時刻。1 件も無ければ <c>null</c>。
/// **「何件あるか」より「いつから詰まっているか」の方が異常に気付ける。**
/// </param>
/// <param name="DeadLetterCount">デッドレターの件数。</param>
/// <param name="OldestDeadLetterAt">デッドレターのうち、最も古い最終試行の時刻。</param>
public sealed record OutboxStatus(
    int PendingCount,
    DateTime? OldestPendingAt,
    int DeadLetterCount,
    DateTime? OldestDeadLetterAt);

/// <summary>画面に出すデッドレター 1 件。</summary>
/// <remarks>
/// **<c>PayloadJson</c> を持たない。** 持たせないことで、
/// 画面へ回答本文が漏れる経路そのものを無くしている（Issue #45）。
/// </remarks>
/// <param name="SurveyTitle">アンケートの題名。**消えたアンケートでは <c>null</c>。**</param>
/// <param name="LastError">最後に失敗した理由。</param>
/// <param name="CreatedAt">回答を受け付けた時刻。**ここからずっと届いていない。**</param>
/// <param name="UpdatedAt">最後に試した時刻。</param>
public sealed record DeadLetterView(
    string ResponseToken,
    Guid SurveyId,
    string? SurveyTitle,
    int SurveyVersion,
    int RetryCount,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>滞留している回答の件数（Issue #72）。</summary>
/// <remarks>
/// <para>
/// **「まだ届いていない回答が何件あるか」**。送信待ちとデッドレターの合計で、
/// <c>_documents/非機能設計.md</c> 2 章の受付停止はこの数字で決まる。
/// </para>
/// <para>
/// **回答数の上限（Issue #53）とは別物。** あちらは「受け付けた総数」で、
/// 送信できた分も含む。こちらは**捌けていない分だけ**なので、
/// 送信が追いついていれば増えない。
/// </para>
/// </remarks>
/// <param name="Total">全アンケートの合計。</param>
/// <param name="BySurvey">
/// アンケートごとの件数。**危ない水準のものだけが入る**
/// （<c>atLeast</c> 未満は問い合わせの時点で捨てている）。
/// **入っていない＝少ない**であって、0 件とは限らない。
/// </param>
public sealed record PendingBacklog(int Total, ImmutableDictionary<Guid, int> BySurvey)
{
    public static readonly PendingBacklog Empty =
        new(0, ImmutableDictionary<Guid, int>.Empty);

    /// <summary>そのアンケートの件数。**閾値未満なら 0 が返る。**</summary>
    public int For(Guid surveyId) => BySurvey.TryGetValue(surveyId, out var count) ? count : 0;
}

/// <summary>デッドレターを読むときの絞り込み。</summary>
public sealed record DeadLetterQuery
{
    /// <summary>読む件数の上限。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>読み飛ばす件数。**ページ送り用。**</summary>
    public int Offset { get; init; }
}

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

    /// <summary>滞留の件数を数える（Issue #72）。</summary>
    /// <param name="perSurveyAtLeast">
    /// アンケートごとの件数を返す下限。**これ未満のアンケートは返らない。**
    /// 全アンケートぶんを返すと、見張りの費用がアンケート数に比例してしまう。
    /// </param>
    /// <param name="cancellationToken">中断。</param>
    Task<PendingBacklog> CountBacklogAsync(
        int perSurveyAtLeast,
        CancellationToken cancellationToken = default);

    /// <summary>滞留の状況をまとめて読む。</summary>
    /// <remarks>
    /// **1 回の問い合わせで済ませる。** 画面を開くたびに件数と最古の時刻を
    /// 別々に数えると、送信待ちの表を何度も走査することになる（Issue #45）。
    /// </remarks>
    Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>デッドレターを新しい順に読む。**回答本文は返さない。**</summary>
    Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
        DeadLetterQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>デッドレターを送信待ちへ戻す。**人が判断して押すもの。**</summary>
    /// <remarks>
    /// <para>
    /// **自動で戻さない。** 通らない理由が残ったまま戻すと、
    /// 上限まで試しては分離するのを繰り返すだけになる。
    /// </para>
    /// <para>
    /// **デッドレターの行しか戻さない**（状態を条件に入れてある）。
    /// 送信中の行を横から書き換えて、ワーカーの確保を壊さないため。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 戻せたなら、その回答が属するアンケートの識別子。
    /// 見つからない（既に戻された・送信できて消えた）なら <c>null</c>。
    /// **監査ログへ何を残すかを呼び出し元が決められるように、識別子を返す**
    /// （<c>ResponseToken</c> は監査ログへ入れない決まり）。
    /// </returns>
    Task<Guid?> RequeueDeadLetterAsync(
        string responseToken,
        CancellationToken cancellationToken = default);
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

    /// <summary>アンケートごとの滞留件数を受ける行。</summary>
    /// <remarks>**<c>COUNT</c> の型が 3 者で違う**（下の <c>OutboxStatusRow</c> と同じ理由）。</remarks>
    private sealed class BacklogRow
    {
        public Guid SurveyId { get; set; }

        public long Count { get; set; }
    }

    public async Task<PendingBacklog> CountBacklogAsync(
        int perSurveyAtLeast,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSurveyAtLeast);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **1 本の接続で 2 回問い合わせる。** 複数の結果集合を 1 回で返す書き方は
        // 3 者で挙動が揃わない。**数えるのは一定間隔に 1 回**なので、
        // ここを 1 往復に縮めても効かない
        var total = await connection.ExecuteScalarAsync<int>(Sql(
            SqlDialect.PendingBacklogTotal,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<BacklogRow>(Sql(
            SqlDialect.PendingBacklogBySurvey,
            new { AtLeast = (long)perSurveyAtLeast },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new PendingBacklog(
            total,
            rows.ToImmutableDictionary(row => row.SurveyId, row => (int)row.Count));
    }

    /// <summary>集約の結果を受ける行。</summary>
    /// <remarks>
    /// <para>
    /// **<c>COUNT</c> の型が 3 者で違う。** SQL Server は <c>int</c>、
    /// PostgreSQL と MySQL は <c>bigint</c>。
    /// </para>
    /// <para>
    /// **位置引数の record で受けないこと。** Dapper は引数の型で組み立て先を探すので、
    /// <c>long</c> と書くと SQL Server で、<c>int</c> と書くと他の 2 つで
    /// 「合う組み立て方が無い」と言って落ちる。
    /// **書き換え可能な属性にすると、Dapper が型を合わせて入れてくれる。**
    /// </para>
    /// <para>
    /// SQL 側で <c>CAST</c> して揃える手もあるが、**その書き方も 3 者で違う**
    /// （<c>BIGINT</c> / <c>SIGNED</c>）ので、受け側で吸収する方が短い。
    /// </para>
    /// </remarks>
    private sealed class OutboxStatusRow
    {
        public long PendingCount { get; set; }

        public DateTime? OldestPendingAt { get; set; }

        public long DeadLetterCount { get; set; }

        public DateTime? OldestDeadLetterAt { get; set; }
    }

    public async Task<OutboxStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstAsync<OutboxStatusRow>(Sql(
            SqlDialect.OutboxStatus,
            new { DeadLetterStatus = (int)ResponseStatus.DeadLetter },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new OutboxStatus(
            (int)row.PendingCount,
            row.OldestPendingAt,
            (int)row.DeadLetterCount,
            row.OldestDeadLetterAt);
    }

    public async Task<IReadOnlyList<DeadLetterView>> ListDeadLettersAsync(
        DeadLetterQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DeadLetterView>(Sql(
            SqlDialect.ListDeadLetters(Provider),
            new
            {
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
                Limit = query.Limit,
                Offset = query.Offset,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    /// <summary>戻す対象を見分けるために読む列。</summary>
    /// <remarks>
    /// **単独の値ではなく行として読む。** <c>Guid</c> は MySQL では
    /// <c>CHAR(36)</c> に入っており、単独の値として読むと変換が効かない。
    /// </remarks>
    private sealed record DeadLetterKey(Guid SurveyId);

    public async Task<Guid?> RequeueDeadLetterAsync(
        string responseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseToken);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **どのアンケートの回答かを先に読む。** 戻したあとでは状態が変わっていて
        // 「デッドレターだった行」として引き直せない。
        // MySQL に RETURNING が無いので、1 文では取れない
        var key = await connection.QueryFirstOrDefaultAsync<DeadLetterKey>(Sql(
            "SELECT [SurveyId] FROM [Responses] "
            + "WHERE [ResponseToken] = @ResponseToken AND [Status] = @DeadLetterStatus",
            new
            {
                ResponseToken = responseToken,
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (key is null)
        {
            return null;
        }

        // **再送の回数を 0 に戻す。** 上限に達して分離された行は、
        // 回数を残したまま戻すと 1 回失敗しただけで再びデッドレターへ落ちる。
        // **原因を直した人が戻すもの**なので、試す回数も最初からにする。
        //
        // **失敗の理由（LastError）は消さない。** 次の試行が上書きするまでは、
        // なぜ止まっていたのかが分かる方がよい。
        //
        // **状態を条件に入れてある。** 2 回押しても 2 件目は 0 行で終わる
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [Responses] SET "
            + "  [Status] = @PendingStatus, "
            + "  [RetryCount] = 0, "
            + "  [NextAttemptAt] = @Now, "
            + "  [LockedBy] = NULL, [LockedUntil] = NULL, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [ResponseToken] = @ResponseToken AND [Status] = @DeadLetterStatus",
            new
            {
                ResponseToken = responseToken,
                PendingStatus = (int)ResponseStatus.Pending,
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 0 ? null : key.SurveyId;
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
