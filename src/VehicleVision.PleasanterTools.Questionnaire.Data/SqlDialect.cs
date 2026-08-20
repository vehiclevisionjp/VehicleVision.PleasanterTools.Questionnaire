namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>RDBMS ごとに書き方が違う部分を閉じ込める。</summary>
/// <remarks>
/// **方言差はここだけに置く。** 呼び出し側へ散らさない
/// （<c>_documents/アーキテクチャ方針.md</c> 13 章）。
/// </remarks>
public static partial class SqlDialect
{
    /// <summary>SQL の中の識別子を、その RDBMS の引用符へ書き換える。</summary>
    /// <remarks>
    /// <para>
    /// **SQL を SQL のまま書けるようにするためのもの。**
    /// 文字列の連結と補間で組み立てると、読むのに頭の中で展開する必要があるうえ、
    /// **引用を忘れても動いてしまう**ので気付けない。
    /// </para>
    /// <code>
    /// Format(provider, "SELECT [PublicId] FROM [Surveys] WHERE [SurveyId] = @SurveyId")
    /// </code>
    /// <para>
    /// **角括弧で囲んだ所だけが書き換わる。** 素の識別子はそのまま残るので、
    /// 囲み忘れが目で見て分かる。SQL Server では書き換えが起きない
    /// （角括弧がそのまま引用符になる）。
    /// </para>
    /// <para>
    /// ⚠️ **文字列リテラルを含む SQL には使わないこと。**
    /// リテラルの中の <c>[</c> まで書き換えてしまう。
    /// 値はパラメータで渡すのが決まりなので、今のところ該当する SQL は無い。
    /// </para>
    /// </remarks>
    public static string Format(DatabaseProvider provider, string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // SQL Server は角括弧がそのまま引用符。**触らない**
        if (provider is DatabaseProvider.SqlServer)
        {
            return sql;
        }

        return IdentifierPattern().Replace(
            sql,
            match => Quote(provider, match.Groups[1].Value));
    }

    /// <summary>角括弧で囲まれた識別子。</summary>
    /// <remarks>
    /// **識別子として妥当な形だけを拾う。** 配列の添字などを巻き込まないため。
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\[([A-Za-z_][A-Za-z0-9_]*)\]",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex IdentifierPattern();

    /// <summary>識別子を引用する。</summary>
    public static string Quote(DatabaseProvider provider, string identifier) => provider switch
    {
        DatabaseProvider.SqlServer => $"[{identifier}]",
        DatabaseProvider.PostgreSql => $"\"{identifier}\"",
        DatabaseProvider.MySql => $"`{identifier}`",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary><c>RETURNING</c> 相当が使えるか。</summary>
    public static bool SupportsReturning(DatabaseProvider provider) =>
        provider is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql;

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
            "RETURNING r.\"ResponseToken\", r.\"SurveyId\", r.\"SurveyVersion\", " +
            "          r.\"PayloadJson\", r.\"RetryCount\"",

        // MySQL は RETURNING が無いので、確保してから読み直す
        DatabaseProvider.MySql =>
            "UPDATE `Responses` " +
            "SET `Status` = @SendingStatus, `LockedBy` = @LockedBy, `LockedUntil` = @LockedUntil " +
            "WHERE `Status` = @PendingStatus AND `NextAttemptAt` <= @Now " +
            "ORDER BY `NextAttemptAt` LIMIT 1",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>件数を絞る句。<c>@Limit</c> と <c>@Offset</c> を使う。</summary>
    /// <remarks>
    /// **書き方が 3 者で違う。** SQL Server は <c>OFFSET/FETCH</c>、
    /// PostgreSQL と MySQL は <c>LIMIT</c>。
    /// **MySQL は <c>OFFSET/FETCH</c> を解さない**（8.4 で確認）。
    /// **どちらも <c>ORDER BY</c> が要る**ので、呼ぶ側で必ず付けること。
    /// </remarks>
    public static string Page(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => "OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY",
        DatabaseProvider.PostgreSql or DatabaseProvider.MySql => "LIMIT @Limit OFFSET @Offset",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary><c>LIKE</c> の逃がし文字。</summary>
    /// <remarks>
    /// **円記号を使わない。** MySQL は文字列リテラルの中でも円記号を逃がし文字として
    /// 解するので、<c>ESCAPE</c> の指定そのものが 3 者で書き分けになる。
    /// **記号を変えれば書き分けが要らない。**
    /// </remarks>
    public const char LikeEscape = '!';

    /// <summary><c>LIKE</c> へ渡す値から記号を逃がす。</summary>
    /// <remarks>
    /// **逃がさないと、利用者が書いた <c>%</c> が「何でも」になる。**
    /// SQL の挿し込みにはならない（値は引数で渡すため）が、
    /// **絞ったつもりで絞れていない**のは、監査ログを読む道具として致命的。
    /// </remarks>
    public static string EscapeLike(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            // **`[` は SQL Server だけの記号。** 3 者で同じ結果にするため常に逃がす
            if (character is LikeEscape or '%' or '_' or '[')
            {
                builder.Append(LikeEscape);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

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
    /// **デッドレターも数える。** 送信待ちと違って**自然に捌けない**ので、
    /// 溜まった行が DB を圧迫することでは同じ。
    /// 除くと「送信待ちは 0 件なのに DB が溢れる」が起きる。
    /// </para>
    /// <para>
    /// **受付のたびには呼ばない。** 見張りが一定間隔で 1 回だけ数える。
    /// </para>
    /// </remarks>
    public const string PendingBacklogTotal = "SELECT COUNT(*) FROM [Responses]";

    /// <summary>閾値に近いアンケートだけを数える SQL（Issue #72）。</summary>
    /// <remarks>
    /// **<c>HAVING</c> で絞る。** 全アンケートぶんの行を返すと、
    /// アンケートが増えるほど見張りの費用が上がる。
    /// **返るのは危ないものだけ**なので、たいていは 0 行で終わる。
    /// </remarks>
    public const string PendingBacklogBySurvey =
        "SELECT [SurveyId] AS [SurveyId], COUNT(*) AS [Count] "
        + "FROM [Responses] "
        + "GROUP BY [SurveyId] "
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
            "  [Status] = @PendingStatus, [NextAttemptAt] = @Now, [RetryCount] = 0, " +
            "  [LastError] = NULL, [LockedBy] = NULL, [LockedUntil] = NULL, [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken; " +
            "IF @@ROWCOUNT = 0 " +
            "INSERT INTO [Responses] " +
            "  ([ResponseToken], [SurveyId], [SurveyVersion], [PayloadJson], [Status], " +
            "   [RetryCount], [NextAttemptAt], [CreatedAt], [UpdatedAt]) " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @Now, @Now);",

        DatabaseProvider.PostgreSql =>
            "INSERT INTO \"Responses\" " +
            "  (\"ResponseToken\", \"SurveyId\", \"SurveyVersion\", \"PayloadJson\", \"Status\", " +
            "   \"RetryCount\", \"NextAttemptAt\", \"CreatedAt\", \"UpdatedAt\") " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @Now, @Now) " +
            "ON CONFLICT (\"ResponseToken\") DO UPDATE SET " +
            "  \"SurveyVersion\" = EXCLUDED.\"SurveyVersion\", " +
            "  \"PayloadJson\" = EXCLUDED.\"PayloadJson\", " +
            "  \"Status\" = EXCLUDED.\"Status\", " +
            "  \"NextAttemptAt\" = EXCLUDED.\"NextAttemptAt\", " +
            "  \"RetryCount\" = 0, \"LastError\" = NULL, " +
            "  \"LockedBy\" = NULL, \"LockedUntil\" = NULL, " +
            "  \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\"",

        DatabaseProvider.MySql =>
            "INSERT INTO `Responses` " +
            "  (`ResponseToken`, `SurveyId`, `SurveyVersion`, `PayloadJson`, `Status`, " +
            "   `RetryCount`, `NextAttemptAt`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES (@ResponseToken, @SurveyId, @SurveyVersion, @PayloadJson, @PendingStatus, " +
            "        0, @Now, @Now, @Now) " +
            "ON DUPLICATE KEY UPDATE " +
            "  `SurveyVersion` = VALUES(`SurveyVersion`), " +
            "  `PayloadJson` = VALUES(`PayloadJson`), " +
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
        "  ([ResponseToken], [SurveyId], [PleasanterReferenceId], [CreatedAt], [UpdatedAt]) " +
        "VALUES (@ResponseToken, @SurveyId, NULL, @Now, @Now)";


    /// <summary>トークンと <c>ReferenceId</c> の対応を保存する SQL。</summary>
    public static string SaveResponseToken(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE [ResponseTokens] " +
            "SET [PleasanterReferenceId] = @ReferenceId, [UpdatedAt] = @Now " +
            "WHERE [ResponseToken] = @ResponseToken; " +
            "IF @@ROWCOUNT = 0 " +
            "INSERT INTO [ResponseTokens] " +
            "  ([ResponseToken], [SurveyId], [PleasanterReferenceId], [CreatedAt], [UpdatedAt]) " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @Now, @Now);",

        DatabaseProvider.PostgreSql =>
            "INSERT INTO \"ResponseTokens\" " +
            "  (\"ResponseToken\", \"SurveyId\", \"PleasanterReferenceId\", \"CreatedAt\", \"UpdatedAt\") " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @Now, @Now) " +
            "ON CONFLICT (\"ResponseToken\") DO UPDATE SET " +
            "  \"PleasanterReferenceId\" = EXCLUDED.\"PleasanterReferenceId\", " +
            "  \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\"",

        DatabaseProvider.MySql =>
            "INSERT INTO `ResponseTokens` " +
            "  (`ResponseToken`, `SurveyId`, `PleasanterReferenceId`, `CreatedAt`, `UpdatedAt`) " +
            "VALUES (@ResponseToken, @SurveyId, @ReferenceId, @Now, @Now) " +
            "ON DUPLICATE KEY UPDATE " +
            "  `PleasanterReferenceId` = VALUES(`PleasanterReferenceId`), " +
            "  `UpdatedAt` = VALUES(`UpdatedAt`)",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };
}
