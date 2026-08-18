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

    /// <summary>現在時刻（UTC）を返す式。</summary>
    /// <remarks>
    /// **アプリ側で UTC を渡すのが基本。** これは DB 側で時刻を入れる必要がある場合だけ使う。
    /// MySQL の <c>datetime</c> はタイムゾーンを持たないため、
    /// **必ず UTC に正規化して格納する**（<c>_documents/データモデル設計.md</c> 4 章）。
    /// </remarks>
    public static string UtcNow(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => "SYSUTCDATETIME()",
        DatabaseProvider.PostgreSql => "(NOW() AT TIME ZONE 'utc')",
        DatabaseProvider.MySql => "UTC_TIMESTAMP(6)",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>
    /// 送信待ちの行を 1 件だけ確保する SQL。
    /// **取り出しと状態更新を 1 文で行う。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// スケールアウトすると複数のインスタンスが同じ行を拾う。
    /// **`Create` は冪等でないので排他は必須**（<c>_documents/アーキテクチャ方針.md</c> 10 章）。
    /// </para>
    /// <para>
    /// **書き方が 3 者でまったく違う。** SQL Server は <c>OUTPUT</c> 付きの <c>UPDATE</c>、
    /// PostgreSQL は <c>FOR UPDATE SKIP LOCKED</c> の副問い合わせ、
    /// MySQL は同じ考え方だが <c>RETURNING</c> が無いので 2 文に分ける。
    /// </para>
    /// </remarks>
    public static string ClaimPendingResponse(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => """
            UPDATE TOP (1) [Responses]
            SET [Status] = @SendingStatus,
                [LockedBy] = @LockedBy,
                [LockedUntil] = @LockedUntil
            OUTPUT inserted.[ResponseToken], inserted.[SurveyId], inserted.[SurveyVersion],
                   inserted.[PayloadJson], inserted.[RetryCount]
            WHERE [Status] = @PendingStatus
              AND [NextAttemptAt] <= @Now
            """,

        DatabaseProvider.PostgreSql => """
            UPDATE "Responses" AS r
            SET "Status" = @SendingStatus,
                "LockedBy" = @LockedBy,
                "LockedUntil" = @LockedUntil
            WHERE r."ResponseToken" = (
                SELECT c."ResponseToken" FROM "Responses" AS c
                WHERE c."Status" = @PendingStatus AND c."NextAttemptAt" <= @Now
                ORDER BY c."NextAttemptAt"
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            RETURNING r."ResponseToken", r."SurveyId", r."SurveyVersion",
                      r."PayloadJson", r."RetryCount"
            """,

        // MySQL は RETURNING が無いので、確保してから読み直す
        DatabaseProvider.MySql => """
            UPDATE `Responses`
            SET `Status` = @SendingStatus,
                `LockedBy` = @LockedBy,
                `LockedUntil` = @LockedUntil
            WHERE `Status` = @PendingStatus
              AND `NextAttemptAt` <= @Now
            ORDER BY `NextAttemptAt`
            LIMIT 1
            """,

        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>MySQL で確保した行を読み直す SQL。</summary>
    /// <remarks><see cref="ClaimPendingResponse"/> が <c>RETURNING</c> を使えないため。</remarks>
    public const string ReadClaimedResponseForMySql = """
        SELECT `ResponseToken`, `SurveyId`, `SurveyVersion`, `PayloadJson`, `RetryCount`
        FROM `Responses`
        WHERE `Status` = @SendingStatus AND `LockedBy` = @LockedBy
        ORDER BY `NextAttemptAt`
        LIMIT 1
        """;

    /// <summary><c>RETURNING</c> 相当が使えるか。</summary>
    public static bool SupportsReturning(DatabaseProvider provider) =>
        provider is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql;
}
