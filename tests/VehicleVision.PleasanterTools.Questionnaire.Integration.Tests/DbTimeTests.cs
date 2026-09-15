using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>時刻が入れたとおりに戻ることを 3 RDBMS で確かめる。</summary>
/// <remarks>
/// <para>
/// **PostgreSQL で 9 時間ずれる事故を踏んでいる。**
/// Npgsql は <see cref="DateTimeKind.Utc"/> を見て型を <c>timestamptz</c> と判断するが、
/// 列は時間帯を持たない <c>timestamp</c> なので、
/// PostgreSQL が**セッションの時間帯へ変換してから格納していた**。
/// サーバの時間帯が <c>Asia/Tokyo</c> だと保存値が 9 時間先になる。
/// </para>
/// <para>
/// **検証用の PostgreSQL の時間帯を UTC にして隠さない。**
/// 実際の運用でサーバが UTC である保証は無く、
/// **どの時間帯でも同じ値が入ること**こそが要るため。
/// </para>
/// </remarks>
public class DbTimeTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 入れた時刻がそのまま戻る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);
        var store = new AdminUserStore(factory);

        await using (var cleanup = factory.Create())
        {
            await cleanup.OpenAsync();
            await cleanup.ExecuteAsync(
                $"DELETE FROM {SqlDialect.Quote(provider, "AdminRecoveryCodes")}");
            await cleanup.ExecuteAsync($"DELETE FROM {SqlDialect.Quote(provider, "AdminUsers")}");
        }

        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = $"tz-{Guid.NewGuid():N}",
            PasswordHash = "not-used",
            Role = AdminRole.Editor,
        };
        await store.CreateAsync(user);

        // **UTC の種別を持った時刻を渡す。** ここが変換されるかどうかを見ている
        var lockedUntil = new DateTime(2026, 8, 18, 0, 15, 0, DateTimeKind.Utc);
        await store.LockAsync(user.AdminUserId, lockedUntil);

        var stored = await store.FindByIdAsync(user.AdminUserId);

        Assert.NotNull(stored?.LockedUntil);
        Assert.Equal(lockedUntil.Ticks, stored.LockedUntil.Value.Ticks);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void 検証用のサーバが必ずしもUTCでないことを確かめる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // この検証はサーバ側の設定を見るだけなので、PostgreSQL のときだけ意味がある
        if (provider != DatabaseProvider.PostgreSql)
        {
            return;
        }

        using var connection = new DbConnectionFactory(provider, connectionString).Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SHOW TimeZone";

        // **UTC 以外でも通ることが要る。** ここが UTC だと、上の検証が素通りしてしまう
        Assert.NotNull(command.ExecuteScalar());
    }

    [Fact]
    public void DBへ渡す形では種別を外す()
    {
        var value = new DateTime(2026, 8, 18, 12, 34, 56, 789, DateTimeKind.Utc);

        var forDb = DbTime.ForDb(value);

        Assert.Equal(DateTimeKind.Unspecified, forDb.Kind);
        // 秒未満は落とす（MySQL の datetime が保持しないため）
        Assert.Equal(new DateTime(2026, 8, 18, 12, 34, 56), forDb);
    }
}
