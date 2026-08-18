using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>3 RDBMS へ同じスキーマを流せることを確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 設定していない場合は何も検証せずに終わる。**緑を「通った」と読まないこと。**
/// </para>
/// <para>
/// 起動は <c>docker compose --profile postgres --profile mysql up -d --wait</c>。
/// </para>
/// </remarks>
public class DatabaseMigrationTests
{
    private const string Password = "Questionnaire#Test1";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() => new()
    {
        {
            DatabaseProvider.SqlServer,
            $"Server=localhost,11433;Database=Questionnaire;UID=sa;PWD={Password};TrustServerCertificate=True"
        },
        {
            DatabaseProvider.PostgreSql,
            $"Host=localhost;Port=15432;Database=questionnaire;Username=postgres;Password={Password}"
        },
        {
            DatabaseProvider.MySql,
            $"Server=localhost;Port=13306;Database=questionnaire;Uid=root;Pwd={Password}"
        },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void 同じ定義から三つのRDBMSへスキーマを流せる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);

        // 2 回流しても壊れない（未適用のものだけ適用される）
        DatabaseMigrator.MigrateUp(provider, connectionString);

        Assert.False(DatabaseMigrator.HasPendingMigrations(provider, connectionString));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 送信待ちの行を排他的に確保できる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);

        var factory = new DbConnectionFactory(provider, connectionString);
        using var connection = factory.Create();
        connection.Open();

        var token = $"tok-{Guid.NewGuid():N}"[..32];
        var now = DateTime.UtcNow;

        connection.Execute(
            $"""
            INSERT INTO {SqlDialect.Quote(provider, "Responses")}
                ({SqlDialect.Quote(provider, "ResponseToken")},
                 {SqlDialect.Quote(provider, "SurveyId")},
                 {SqlDialect.Quote(provider, "SurveyVersion")},
                 {SqlDialect.Quote(provider, "PayloadJson")},
                 {SqlDialect.Quote(provider, "Status")},
                 {SqlDialect.Quote(provider, "RetryCount")},
                 {SqlDialect.Quote(provider, "NextAttemptAt")},
                 {SqlDialect.Quote(provider, "CreatedAt")},
                 {SqlDialect.Quote(provider, "UpdatedAt")})
            VALUES (@Token, @SurveyId, 1, @Payload, 0, 0, @Now, @Now, @Now)
            """,
            new
            {
                Token = token,
                SurveyId = Guid.NewGuid(),
                Payload = "{}",
                Now = now.AddMinutes(-1),
            });

        var claimed = Claim(connection, provider, "worker-1", now);

        Assert.NotNull(claimed);
        Assert.Equal(token, claimed);

        // **2 回目は取れない。** 確保済みの行を別のワーカーが拾わないこと
        var second = Claim(connection, provider, "worker-2", now);
        Assert.NotEqual(token, second);

        connection.Execute(
            $"DELETE FROM {SqlDialect.Quote(provider, "Responses")} "
            + $"WHERE {SqlDialect.Quote(provider, "ResponseToken")} = @Token",
            new { Token = token });
    }

    private static string? Claim(
        System.Data.Common.DbConnection connection,
        DatabaseProvider provider,
        string lockedBy,
        DateTime now)
    {
        var parameters = new
        {
            SendingStatus = 1,
            PendingStatus = 0,
            LockedBy = lockedBy,
            LockedUntil = now.AddMinutes(5),
            Now = now,
        };

        if (SqlDialect.SupportsReturning(provider))
        {
            return connection.QueryFirstOrDefault<string>(
                SqlDialect.ClaimPendingResponse(provider), parameters);
        }

        // MySQL は RETURNING が無いので、確保してから読み直す
        var affected = connection.Execute(SqlDialect.ClaimPendingResponse(provider), parameters);
        return affected == 0
            ? null
            : connection.QueryFirstOrDefault<string>(
                SqlDialect.ReadClaimedResponseForMySql, parameters);
    }
}
