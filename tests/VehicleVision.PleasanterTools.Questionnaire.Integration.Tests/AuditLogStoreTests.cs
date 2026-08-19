using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理操作の記録を 3 RDBMS へ書けること。</summary>
/// <remarks>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// 設定していない場合は何も検証せずに終わる。**緑を「通った」と読まないこと。**
/// </remarks>
public class AuditLogStoreTests
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
    public async Task 成功も失敗も残る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);

        var factory = new DbConnectionFactory(provider, connectionString);
        await ClearAsync(factory).ConfigureAwait(true);

        var store = new AuditLogStore(factory);
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();

        await store.WriteAsync(new AuditEntry(
            DateTime.Now,
            actor,
            "POST /api/admin/users/{adminUserId}/role",
            StatusCodes200,
            "AdminUser",
            target.ToString(),
            """{"role":"Editor"}""",
            "203.0.113.10")).ConfigureAwait(true);

        // **断られた試みも残る。** 残っていないと「起きなかった」と区別できない
        await store.WriteAsync(new AuditEntry(
            DateTime.Now.AddSeconds(1),
            actor,
            "POST /api/admin/users/{adminUserId}/disable",
            StatusCodes409,
            "AdminUser",
            target.ToString(),
            null,
            "203.0.113.10")).ConfigureAwait(true);

        var rows = await store.ListAsync(10).ConfigureAwait(true);

        Assert.Equal(2, rows.Count);

        // **新しい順**
        Assert.Equal(StatusCodes409, rows[0].StatusCode);
        Assert.Equal(StatusCodes200, rows[1].StatusCode);

        Assert.All(rows, row => Assert.Equal(actor, row.AdminUserId));
        Assert.All(rows, row => Assert.Equal("AdminUser", row.TargetType));
        Assert.All(rows, row => Assert.Equal(target.ToString(), row.TargetId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 認証を通っていない試みも残る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);

        var factory = new DbConnectionFactory(provider, connectionString);
        await ClearAsync(factory).ConfigureAwait(true);

        var store = new AuditLogStore(factory);

        // **合言葉が違えば誰なのか分からない。** それでも試みは残す
        await store.WriteAsync(new AuditEntry(
            DateTime.Now,
            AdminUserId: null,
            "POST /api/admin/login",
            StatusCodes401,
            DetailJson: """{"loginId":"admin"}""",
            IpAddress: "203.0.113.99")).ConfigureAwait(true);

        var rows = await store.ListAsync(10).ConfigureAwait(true);

        var row = Assert.Single(rows);
        Assert.Null(row.AdminUserId);
        Assert.Equal(StatusCodes401, row.StatusCode);
        Assert.Equal("203.0.113.99", row.IpAddress);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 桁を超える値は切り詰めて残す(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);

        var factory = new DbConnectionFactory(provider, connectionString);
        await ClearAsync(factory).ConfigureAwait(true);

        var store = new AuditLogStore(factory);

        // **記録できなかったより、切り詰めた方がまし。**
        // 桁溢れで例外にすると、操作は通ったのに記録だけが落ちる
        await store.WriteAsync(new AuditEntry(
            DateTime.Now,
            AdminUserId: null,
            new string('a', 300),
            StatusCodes200,
            TargetType: new string('b', 300),
            TargetId: new string('c', 300),
            IpAddress: new string('d', 300))).ConfigureAwait(true);

        var row = Assert.Single(await store.ListAsync(10).ConfigureAwait(true));

        Assert.Equal(128, row.Action.Length);
        Assert.Equal(128, row.TargetType!.Length);
        Assert.Equal(128, row.TargetId!.Length);
        Assert.Equal(64, row.IpAddress!.Length);
    }

    private const int StatusCodes200 = 200;

    private const int StatusCodes401 = 401;

    private const int StatusCodes409 = 409;

    /// <summary>**DB を共有しているので、前の試験の行を消してから見る。**</summary>
    private static async Task ClearAsync(IDbConnectionFactory factory)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(
            SqlDialect.Format(factory.Provider, "DELETE FROM [AuditLogs]")).ConfigureAwait(false);
    }
}
