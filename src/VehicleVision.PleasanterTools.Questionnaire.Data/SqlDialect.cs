namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>RDBMS ごとに書き方が違う部分を閉じ込める。</summary>
/// <remarks>
/// **方言差はここだけに置く。** 呼び出し側へ散らさない
/// （<c>_documents/アーキテクチャ方針.md</c> 13 章）。
/// </remarks>
public static class SqlDialect
{
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
