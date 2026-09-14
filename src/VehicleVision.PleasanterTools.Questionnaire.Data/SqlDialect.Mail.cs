namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>メールの送信待ちで、RDBMS ごとに書き方が違う部分（Issue #189）。</summary>
public static partial class SqlDialect
{
    /// <summary>送信待ちのメールを 1 件だけ確保する SQL。</summary>
    /// <remarks>
    /// **回答の確保（<see cref="ClaimPendingResponse"/>）と同じ形。**
    /// SQL Server は <c>OUTPUT</c> 付きの <c>UPDATE</c>、PostgreSQL は
    /// <c>FOR UPDATE SKIP LOCKED</c>、**MySQL は <c>RETURNING</c> が無いので読み直す。**
    /// </remarks>
    public static string ClaimPendingMail(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE TOP (1) [MailOutbox] " +
            "SET [Status] = @SendingStatus, [LockedBy] = @LockedBy, [LockedUntil] = @LockedUntil " +
            "OUTPUT inserted.[MailId], inserted.[Kind], inserted.[SurveyId], " +
            "       inserted.[PayloadProtected], inserted.[RetryCount] " +
            "WHERE [Status] = @PendingStatus AND [NextAttemptAt] <= @Now",

        DatabaseProvider.PostgreSql =>
            "UPDATE \"MailOutbox\" AS m " +
            "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil " +
            "WHERE m.\"MailId\" = (" +
            "  SELECT c.\"MailId\" FROM \"MailOutbox\" AS c " +
            "  WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now " +
            "  ORDER BY c.\"NextAttemptAt\" FOR UPDATE SKIP LOCKED LIMIT 1) " +
            "RETURNING m.\"MailId\", m.\"Kind\", m.\"SurveyId\", " +
            "          m.\"PayloadProtected\", m.\"RetryCount\"",

        DatabaseProvider.MySql =>
            "UPDATE `MailOutbox` " +
            "SET `Status` = @SendingStatus, `LockedBy` = @LockedBy, `LockedUntil` = @LockedUntil " +
            "WHERE `Status` = @PendingStatus AND `NextAttemptAt` <= @Now " +
            "ORDER BY `NextAttemptAt` LIMIT 1",

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>MySQL で、たった今確保した 1 件を読み直す SQL。</summary>
    /// <remarks>
    /// ⚠️ **確保ごとに一意な <c>LockedBy</c> で引く。** 呼び出し元の名前だけで引くと
    /// **過去の確保とも一致する**（回答側で実際に踏んだ）。
    /// </remarks>
    public const string ReadClaimedMailForMySql =
        "SELECT `MailId`, `Kind`, `SurveyId`, `PayloadProtected`, `RetryCount` " +
        "FROM `MailOutbox` WHERE `LockedBy` = @LockedBy";
}
