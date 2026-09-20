using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>配布資産の引換券を 4 RDBMS で確かめる（Issue #318）。</summary>
public class AssetTicketStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private static IAssetTicketStore Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        using var connection = factory.Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, "AssetTickets")}";
        command.ExecuteNonQuery();

        return new AssetTicketStore(factory);
    }

    private static string NewHash() => $"hash-{Guid.NewGuid():N}";

    private static DateTime Now => DateTime.UtcNow;

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限内は何度でも引き換えられる(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var hash = NewHash();
        await store.SaveAsync(hash, "tok-1", surveyId, Now.AddDays(30));

        Assert.NotNull(await store.RedeemAsync(hash, surveyId, Now));
        Assert.NotNull(await store.RedeemAsync(hash, surveyId, Now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れと別アンケートでは引き換えられない(
        DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var store = Create(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var expired = NewHash();
        var active = NewHash();
        await store.SaveAsync(expired, "tok-1", surveyId, Now.AddMinutes(-1));
        await store.SaveAsync(active, "tok-2", surveyId, Now.AddDays(30));

        Assert.Null(await store.RedeemAsync(expired, surveyId, Now));
        Assert.Null(await store.RedeemAsync(active, Guid.NewGuid(), Now));
    }
}
