namespace VehicleVision.PleasanterTools.Questionnaire.Data;

public static partial class SqlDialect
{
    public static string ClaimPendingAssetHistory(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "UPDATE TOP (1) [AssetHistoryOutbox] "
            + "SET [Status] = @SendingStatus, [LockedBy] = @LockedBy, [LockedUntil] = @LockedUntil "
            + "OUTPUT inserted.[EventId], inserted.[SurveyId], inserted.[SurveyVersion], "
            + "inserted.[ResponseToken], inserted.[EventType], inserted.[AssetId], "
            + "inserted.[AssetFileName], inserted.[OccurredAt], inserted.[RetryCount] "
            + "WHERE [Status] = @PendingStatus AND [NextAttemptAt] <= @Now",
        DatabaseProvider.PostgreSql =>
            "UPDATE \"AssetHistoryOutbox\" AS h "
            + "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil "
            + "WHERE h.\"EventId\" = (SELECT c.\"EventId\" FROM \"AssetHistoryOutbox\" AS c "
            + "WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now "
            + "ORDER BY c.\"NextAttemptAt\" FOR UPDATE SKIP LOCKED LIMIT 1) "
            + "RETURNING \"EventId\", \"SurveyId\", \"SurveyVersion\", \"ResponseToken\", "
            + "\"EventType\", \"AssetId\", \"AssetFileName\", \"OccurredAt\", \"RetryCount\"",
        DatabaseProvider.Sqlite =>
            "UPDATE \"AssetHistoryOutbox\" AS h "
            + "SET \"Status\" = @SendingStatus, \"LockedBy\" = @LockedBy, \"LockedUntil\" = @LockedUntil "
            + "WHERE h.rowid = (SELECT c.rowid FROM \"AssetHistoryOutbox\" AS c "
            + "WHERE c.\"Status\" = @PendingStatus AND c.\"NextAttemptAt\" <= @Now "
            + "ORDER BY c.\"NextAttemptAt\" LIMIT 1) "
            + "RETURNING \"EventId\", \"SurveyId\", \"SurveyVersion\", \"ResponseToken\", "
            + "\"EventType\", \"AssetId\", \"AssetFileName\", \"OccurredAt\", \"RetryCount\"",
        DatabaseProvider.MySql =>
            "UPDATE `AssetHistoryOutbox` SET `Status` = @SendingStatus, `LockedBy` = @LockedBy, "
            + "`LockedUntil` = @LockedUntil WHERE `Status` = @PendingStatus "
            + "AND `NextAttemptAt` <= @Now ORDER BY `NextAttemptAt` LIMIT 1",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    public const string ReadClaimedAssetHistoryForMySql =
        "SELECT `EventId`, `SurveyId`, `SurveyVersion`, `ResponseToken`, `EventType`, "
        + "`AssetId`, `AssetFileName`, `OccurredAt`, `RetryCount` "
        + "FROM `AssetHistoryOutbox` WHERE `LockedBy` = @LockedBy";
}
