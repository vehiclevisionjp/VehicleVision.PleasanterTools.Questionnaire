using VehicleVision.PleasanterTools.Questionnaire.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

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

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 別インスタンスの設定キャッシュはTTL後にDBの更新へ追いつく(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        DatabaseMigrator.MigrateUp(provider, connectionString);
        var store = new AppSettingStore(new DbConnectionFactory(provider, connectionString));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z"));
        var configuration = new ConfigurationBuilder().Build();
        var protector = new SecretProtector(Convert.ToBase64String(new byte[32]));
        var first = new AppSettingsProvider(configuration, store, protector, time);
        var second = new AppSettingsProvider(configuration, store, protector, time);
        var initial = $"初期値-{Guid.NewGuid():N}";
        var changed = $"更新値-{Guid.NewGuid():N}";

        await second.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = initial,
            },
            Guid.NewGuid());
        Assert.Equal(initial, (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);

        await second.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = changed,
            },
            Guid.NewGuid());
        Assert.Equal(initial, (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);

        time.Advance(AppSettingsProvider.CacheLifetime);
        Assert.Equal(changed, (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
    }
}
