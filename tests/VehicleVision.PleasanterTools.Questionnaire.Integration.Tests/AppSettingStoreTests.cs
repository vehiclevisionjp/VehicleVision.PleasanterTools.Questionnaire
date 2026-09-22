using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>アプリケーション設定の保管を 4 RDBMS で確かめる（Issue #372）。</summary>
public class AppSettingStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION_CONNECTIONSTRING");
        var providerName =
            Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION_PROVIDER");
        if (!string.IsNullOrWhiteSpace(connectionString)
            && Enum.TryParse<DatabaseProvider>(providerName, ignoreCase: true, out var provider))
        {
            return new TheoryData<DatabaseProvider, string>
            {
                { provider, connectionString },
            };
        }

        return DatabaseMigrationTests.Providers();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 設定を保存して同じ鍵を更新できる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var store = new AppSettingStore(new DbConnectionFactory(provider, connectionString));
        var firstAdminUserId = Guid.NewGuid();
        var secondAdminUserId = Guid.NewGuid();
        var key = $"TEST_SETTING_{Guid.NewGuid():N}";

        await store.SaveAsync(key, "最初の値", false, firstAdminUserId);
        var first = (await store.ListAsync()).Single(record => record.SettingKey == key);

        Assert.Equal("最初の値", first.Value);
        Assert.False(first.IsSecret);
        Assert.Equal(firstAdminUserId, first.UpdatedByAdminUserId);

        await store.SaveAsync(key, "更新後の値", true, secondAdminUserId);
        var updated = (await store.ListAsync()).Single(record => record.SettingKey == key);

        Assert.Equal("更新後の値", updated.Value);
        Assert.True(updated.IsSecret);
        Assert.Equal(secondAdminUserId, updated.UpdatedByAdminUserId);
        Assert.True(updated.UpdatedAt >= first.UpdatedAt);
    }
}
