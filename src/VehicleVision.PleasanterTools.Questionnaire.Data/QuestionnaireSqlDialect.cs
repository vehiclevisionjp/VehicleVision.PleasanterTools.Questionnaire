using static VehicleVision.PleasanterTools.Core.Data.SqlDialect;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>アンケート固有の SQL（RDBMS ごとに書き方が違う部分）。</summary>
/// <remarks>
/// **汎用の方言差（<c>Format</c> / <c>Quote</c> / <c>Page</c> など）は Core.Data の <c>SqlDialect</c> に置く。**
/// <c>static partial class</c> は別のアセンブリへまたげないので、アプリ固有の SQL はこのクラスへ分けた。
/// 汎用側は <c>using static</c> で、そのまま呼べる。
/// </remarks>
public static partial class QuestionnaireSqlDialect
{
    /// <summary>
    /// 送信待ちの行を 1 件だけ確保する SQL。
    /// **取り出しと状態更新を 1 文で行う。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// スケールアウトすると複数のインスタンスが同じ行を拾う。
    /// **<c>Create</c> は冪等でないので排他は必須**（<c>_documents/アーキテクチャ方針.md</c> 10 章）。
    /// </para>
    /// <para>
    /// **書き方が 3 者でまったく違う。** SQL Server は <c>OUTPUT</c> 付きの <c>UPDATE</c>、
    /// PostgreSQL は <c>FOR UPDATE SKIP LOCKED</c> の副問い合わせ、
    /// MySQL は <c>RETURNING</c> が無いので 2 文に分ける。
    /// </para>
    /// </remarks>
    public static string ClaimPendingResponse(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE TOP (1) [Responses] " +
            "SET [Status] = @SendingStatus, [LockedBy] = @LockedBy, [LockedUntil] = @LockedUntil " +
            "OUTPUT inserted.[ResponseToken], inserted.[SurveyId], inserted.[SurveyVersion], " +
            "       inserted.[PayloadJson], inserted.[RetryCount] " +
            "WHERE [Status] = @PendingStatus AND [NextAttemptAt] <= @Now",

        DatabaseProvider.PostgreSql =>
            "UPDATE \"Responses\" AS r " +
            "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil " +
            "WHERE r.\"ResponseToken\" = (" +
            "  SELECT c.\"ResponseToken\" FROM \"Responses\" AS c " +
            "  WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now " +
            "  ORDER BY c.\"NextAttemptAt\" FOR UPDATE SKIP LOCKED LIMIT 1) " +
            "RETURNING \"ResponseToken\", \"SurveyId\", \"SurveyVersion\", " +
            "          \"PayloadJson\", \"RetryCount\"",

        DatabaseProvider.Sqlite =>
            "UPDATE \"Responses\" AS r " +
            "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil " +
            "WHERE r.rowid = (" +
            "  SELECT c.rowid FROM \"Responses\" AS c " +
            "  WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now " +
            "  ORDER BY c.\"NextAttemptAt\" LIMIT 1) " +
            "RETURNING \"ResponseToken\", \"SurveyId\", \"SurveyVersion\", " +
            "          \"PayloadJson\", \"RetryCount\"",

        // MySQL は RETURNING が無いので、確保してから読み直す
        DatabaseProvider.MySql =>
            "UPDATE `Responses` " +
            "SET `Status` = @SendingStatus, `LockedBy` = @LockedBy, `LockedUntil` = @LockedUntil " +
            "WHERE `Status` = @PendingStatus AND `NextAttemptAt` <= @Now " +
            "ORDER BY `NextAttemptAt` LIMIT 1",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>滞留の状況を 1 回で読む SQL。</summary>
    /// <remarks>
    /// <para>
    /// **件数と最古の時刻を別々に数えない**（Issue #45）。画面を開くたびに
    /// 送信待ちの表を 4 回走査することになる。
    /// <c>CASE</c> を使った集約は 3 者とも同じ書き方で通る。
    /// </para>
    /// <para>
    /// **1 行しか返らない**（<c>GROUP BY</c> が無い集約）。表が空でも
    /// 件数は 0、時刻は <c>NULL</c> の行が返る。
    /// </para>
    /// <para>
    /// **数える型が 3 者で違う。** <c>COUNT</c> は SQL Server が <c>int</c>、
    /// PostgreSQL と MySQL が <c>bigint</c> なので、受け側は 64 ビットで取る。
    /// </para>
    /// </remarks>
    public const string OutboxStatus =
        "SELECT "
        + "  COUNT(CASE WHEN [Status] <> @DeadLetterStatus THEN 1 END) AS [PendingCount], "
        + "  MIN(CASE WHEN [Status] <> @DeadLetterStatus THEN [CreatedAt] END) AS [OldestPendingAt], "
        + "  COUNT(CASE WHEN [Status] = @DeadLetterStatus THEN 1 END) AS [DeadLetterCount], "
        + "  MIN(CASE WHEN [Status] = @DeadLetterStatus THEN [UpdatedAt] END) AS [OldestDeadLetterAt] "
        + "FROM [Responses]";

    /// <summary>滞留している回答の総件数を数える SQL（Issue #72）。</summary>
    /// <remarks>
    /// <para>
    /// **アーカイブしていないアンケートのデッドレターも数える。**
    /// 送信待ちと違って**自然に捌けない**ので、
    /// 溜まった行が DB を圧迫することでは同じ。
    /// 除くと「送信待ちは 0 件なのに DB が溢れる」が起きる。
    /// </para>
    /// <para>
    /// **受付のたびには呼ばない。** 見張りが一定間隔で 1 回だけ数える。
    /// </para>
    /// </remarks>
    public const string PendingBacklogTotal =
        "SELECT COUNT(*) FROM [Responses] r "
        + "LEFT JOIN [Surveys] s ON s.[SurveyId] = r.[SurveyId] "
        + "WHERE r.[IsTest] = @IsTest AND s.[ArchivedAt] IS NULL";

    /// <summary>閾値に近いアンケートだけを数える SQL（Issue #72）。</summary>
    /// <remarks>
    /// **<c>HAVING</c> で絞る。** 全アンケートぶんの行を返すと、
    /// アンケートが増えるほど見張りの費用が上がる。
    /// **アーカイブ済みは受付しないため除く。**
    /// **返るのは危ないものだけ**なので、たいていは 0 行で終わる。
    /// </remarks>
    public const string PendingBacklogBySurvey =
        "SELECT r.[SurveyId] AS [SurveyId], COUNT(*) AS [Count] "
        + "FROM [Responses] r "
        + "LEFT JOIN [Surveys] s ON s.[SurveyId] = r.[SurveyId] "
        + "WHERE r.[IsTest] = @IsTest AND s.[ArchivedAt] IS NULL "
        + "GROUP BY r.[SurveyId] "
        + "HAVING COUNT(*) >= @AtLeast";

    /// <summary>デッドレターを新しい順に読む SQL。</summary>
    /// <remarks>
    /// <para>
    /// **<c>PayloadJson</c> を選ばない。** 回答本文には個人情報が入り得るので、
    /// 画面へ渡る経路に載せない（Issue #45）。
    /// </para>
    /// <para>
    /// **<c>LEFT JOIN</c>。** アンケートが消えていても、
    /// 届いていない回答が残っていることは見えなければならない。
    /// </para>
    /// <para>
    /// **並びを 2 本の列で決める。** 時刻は秒までしか持たない（<see cref="DbTime"/>）ので、
    /// 同じ秒の行が複数あるとページ送りで取りこぼす。
    /// </para>
    /// </remarks>
    public static string ListDeadLetters(DatabaseProvider provider) =>
        "SELECT r.[ResponseToken], r.[SurveyId], s.[Title] AS [SurveyTitle], "
        + "       r.[SurveyVersion], r.[RetryCount], r.[LastError], "
        + "       r.[CreatedAt], r.[UpdatedAt] "
        + "FROM [Responses] r LEFT JOIN [Surveys] s ON s.[SurveyId] = r.[SurveyId] "
        + "WHERE r.[Status] = @DeadLetterStatus "
        + "ORDER BY r.[UpdatedAt] DESC, r.[ResponseToken] DESC "
        + Page(provider);

    /// <summary>添付を弾いた記録を新しい順に読む SQL（Issue #39）。</summary>
    /// <remarks>
    /// <para>
    /// **<c>LEFT JOIN</c>。** アンケートが消えていても、
    /// 弾いた記録が残っていることは見えなければならない。
    /// </para>
    /// <para>
    /// **並びを 2 本の列で決める。** 時刻は秒までしか持たない（<see cref="DbTime"/>）ので、
    /// 同じ秒の行が複数あるとページ送りで取りこぼす。
    /// </para>
    /// </remarks>
    public static string ListAttachmentRejections(DatabaseProvider provider) =>
        "SELECT a.[OccurredAt], a.[SurveyId], s.[Title] AS [SurveyTitle], "
        + "       a.[QuestionId], a.[Reason], a.[FileCount] "
        + "FROM [AttachmentRejections] a "
        + "LEFT JOIN [Surveys] s ON s.[SurveyId] = a.[SurveyId] "
        + "ORDER BY a.[OccurredAt] DESC, a.[AttachmentRejectionId] DESC "
        + Page(provider);

    /// <summary>管理者への知らせを新しい順に読む SQL（Issue #80）。</summary>
    /// <remarks>
    /// <para>
    /// **<c>LEFT JOIN</c>。** アンケートが消えていても、
    /// 知らせが残っていることは見えなければならない。
    /// **アンケートに紐づかない知らせ（<c>Guid.Empty</c>）でも題名は付かない。**
    /// </para>
    /// <para>
    /// **並びを 2 本の列で決める。** 時刻は秒までしか持たない（<see cref="DbTime"/>）ので、
    /// 同じ秒の行が複数あるとページ送りで取りこぼす。
    /// </para>
    /// </remarks>
    public static string ListAdminNotifications(DatabaseProvider provider, bool unreadOnly) =>
        "SELECT n.[AdminNotificationId], n.[Kind], n.[SurveyId], s.[Title] AS [SurveyTitle], "
        + "       n.[Count], n.[FirstOccurredAt], n.[LastOccurredAt], n.[ReadAt] "
        + "FROM [AdminNotifications] n "
        + "LEFT JOIN [Surveys] s ON s.[SurveyId] = n.[SurveyId] "
        + (unreadOnly ? "WHERE n.[ReadAt] IS NULL " : string.Empty)
        + "ORDER BY n.[LastOccurredAt] DESC, n.[AdminNotificationId] DESC "
        + Page(provider);

    /// <summary>MySQL で確保した行を読み直す SQL。</summary>
    /// <remarks><see cref="ClaimPendingResponse"/> が <c>RETURNING</c> を使えないため。</remarks>
    public const string ReadClaimedResponseForMySql =
        "SELECT `ResponseToken`, `SurveyId`, `SurveyVersion`, `PayloadJson`, `RetryCount` " +
        "FROM `Responses` " +
        "WHERE `Status` = @SendingStatus AND `LockedBy` = @LockedBy " +
        "ORDER BY `NextAttemptAt` LIMIT 1";

    /// <summary>送信待ちを保存する SQL。**同じ回答が編集されたら上書きする。**</summary>
    /// <remarks>
    /// 書き方が 3 者で違う。SQL Server は <c>UPDATE</c> して 0 件なら <c>INSERT</c>、
    /// PostgreSQL は <c>ON CONFLICT</c>、MySQL は <c>ON DUPLICATE KEY UPDATE</c>。
    /// **<c>MERGE</c> は使わない**（SQL Server の実装に既知の落とし穴があるため）。
    /// </remarks>
    public static string SaveResponse(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE [Responses] SET " +
            "  [SurveyVersion] = @SurveyVersion, [PayloadJson] = @PayloadJson, " +
            "  [IsTest] = @IsTest, " +
            "  [Status] = @PendingStatus, [NextAttemptAt] = @Now, [RetryCount] = 0, " +
            "  [LastError] = NULL, [LockedBy] = NULL, [LockedUntil] = NULL, [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken; " +
            "IF @@ROWCOUNT = 0 " +
            "INSERT INTO [Responses] " +
            "  ([ResponseToken], [SurveyId], [SurveyVersion], [PayloadJson], [Status], " +
            "   [RetryCount], [NextAttemptAt], [IsTest], [CreatedAt], [UpdatedAt]) " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @IsTest, @Now, @Now);",

        DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite =>
            "INSERT INTO \"Responses\" " +
            "  (\"ResponseToken\", \"SurveyId\", \"SurveyVersion\", \"PayloadJson\", \"Status\", " +
            "   \"RetryCount\", \"NextAttemptAt\", \"IsTest\", \"CreatedAt\", \"UpdatedAt\") " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @IsTest, @Now, @Now) " +
            "ON CONFLICT (\"ResponseToken\") DO UPDATE SET " +
            "  \"SurveyVersion\" = EXCLUDED.\"SurveyVersion\", " +
            "  \"PayloadJson\" = EXCLUDED.\"PayloadJson\", " +
            "  \"IsTest\" = EXCLUDED.\"IsTest\", " +
            "  \"Status\" = EXCLUDED.\"Status\", " +
            "  \"NextAttemptAt\" = EXCLUDED.\"NextAttemptAt\", " +
            "  \"RetryCount\" = 0, \"LastError\" = NULL, " +
            "  \"LockedBy\" = NULL, \"LockedUntil\" = NULL, " +
            "  \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\"",

        DatabaseProvider.MySql =>
            "INSERT INTO `Responses` " +
            "  (`ResponseToken`, `SurveyId`, `SurveyVersion`, `PayloadJson`, `Status`, " +
            "   `RetryCount`, `NextAttemptAt`, `IsTest`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @IsTest, @Now, @Now) " +
            "ON DUPLICATE KEY UPDATE " +
            "  `SurveyVersion` = VALUES(`SurveyVersion`), " +
            "  `PayloadJson` = VALUES(`PayloadJson`), " +
            "  `IsTest` = VALUES(`IsTest`), " +
            "  `Status` = VALUES(`Status`), " +
            "  `NextAttemptAt` = VALUES(`NextAttemptAt`), " +
            "  `RetryCount` = 0, `LastError` = NULL, " +
            "  `LockedBy` = NULL, `LockedUntil` = NULL, " +
            "  `UpdatedAt` = VALUES(`UpdatedAt`)",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>回答トークンの行を、無ければ作る SQL。</summary>
    /// <remarks>
    /// <para>
    /// **作ったかどうかを「影響した行数」で判断しない。**
    /// MySQL は既定で「一致した行数」を返す（<c>UseAffectedRows=false</c>）ので、
    /// <c>ON DUPLICATE KEY UPDATE</c> で値を変えなくても 1 行と報告する。
    /// **SQL Server と PostgreSQL では 0 行**なので、同じ判定が 3 者で割れる。
    /// </para>
    /// <para>
    /// **素直に入れて、重複なら入れ直さない。** 作ったかどうかは、
    /// 呼ぶ側が主キーの衝突で見分ける（<c>ResponseTokenStore.EnsureAsync</c>）。
    /// **DB が一意性を守るので、競合しても二重には入らない。**
    /// </para>
    /// </remarks>
    public const string InsertResponseToken =
        "INSERT INTO [ResponseTokens] " +
        "  ([ResponseToken], [SurveyId], [PleasanterReferenceId], [IsTest], [CreatedAt], [UpdatedAt]) " +
        "VALUES (@ResponseToken, @SurveyId, NULL, @IsTest, @Now, @Now)";

    /// <summary>トークンと <c>ReferenceId</c> の対応を保存する SQL。</summary>
    public static string SaveResponseToken(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE [ResponseTokens] " +
            "SET [PleasanterReferenceId] = @ReferenceId, [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken; " +
            "IF @@ROWCOUNT = 0 " +
            "INSERT INTO [ResponseTokens] " +
            "  ([ResponseToken], [SurveyId], [PleasanterReferenceId], [IsTest], [CreatedAt], [UpdatedAt]) " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @IsTest, @Now, @Now);",

        DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite =>
            "INSERT INTO \"ResponseTokens\" " +
            "  (\"ResponseToken\", \"SurveyId\", \"PleasanterReferenceId\", \"IsTest\", \"CreatedAt\", \"UpdatedAt\") " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @IsTest, @Now, @Now) " +
            "ON CONFLICT (\"ResponseToken\") DO UPDATE SET " +
            "  \"PleasanterReferenceId\" = EXCLUDED.\"PleasanterReferenceId\", " +
            "  \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\"",

        DatabaseProvider.MySql =>
            "INSERT INTO `ResponseTokens` " +
            "  (`ResponseToken`, `SurveyId`, `PleasanterReferenceId`, `IsTest`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @IsTest, @Now, @Now) " +
            "ON DUPLICATE KEY UPDATE " +
            "  `PleasanterReferenceId` = VALUES(`PleasanterReferenceId`), " +
            "  `UpdatedAt` = VALUES(`UpdatedAt`)",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };
}
