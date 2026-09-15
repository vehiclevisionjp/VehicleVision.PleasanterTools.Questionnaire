using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web;

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

        // **足りないものを名前で言えること。** 「当て忘れている」だけでは直せない
        Assert.Empty(DatabaseMigrator.PendingMigrations(provider, connectionString));
    }

    [Fact]
    public async Task 繋がらないDBは待った末に理由を返す()
    {
        // **DB は要らない。** 届かない宛先へ繋ぎに行くだけ
        var failure = await DatabaseMigrator.WaitForDatabaseAsync(
            DatabaseProvider.PostgreSql,
            // 予約済みの記録用アドレス（RFC 5737）。**誰も応答しない**
            "Host=192.0.2.1;Port=15432;Database=q;Username=postgres;Password=x;Timeout=1",
            TimeSpan.FromSeconds(2));

        // **黙って先へ進まないこと。** 進むと FluentMigrator 側で分かりにくく落ちる
        Assert.NotNull(failure);
    }

    [Fact]
    public void 合図が無ければマイグレーションの起動ではない()
    {
        Assert.False(MigrationCommand.IsRequested(["--generate-secret-key"]));
        Assert.True(MigrationCommand.IsRequested(["--migrate"]));
        Assert.True(MigrationCommand.IsRequested(["--migrate-status"]));

        // **前方一致で拾わない。** --migrate-status を --migrate と読むと、
        // 「見るだけ」のつもりが当ててしまう
        Assert.False(MigrationCommand.IsRequested(["--migrated"]));
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

        // **先に空にする。** この試験は「最も古い 1 件が取れる」を見るので、
        // 他の試験が残した行があると、そちらが取れて落ちる。
        // **落ちる場所とこの試験の中身は無関係**なので、原因を辿るのに時間が掛かる
        connection.Execute($"DELETE FROM {SqlDialect.Quote(provider, "Responses")}");

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
