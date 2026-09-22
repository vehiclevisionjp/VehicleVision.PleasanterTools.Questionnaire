using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>アプリケーション設定の優先順位とスナップショット（Issue #372）。</summary>
public class AppSettingsProviderTests
{
    private static readonly SecretProtector Protector =
        new(Convert.ToBase64String(new byte[32]));

    private sealed class FakeStore(params AppSettingRecord[] records) : IAppSettingStore
    {
        private readonly Dictionary<string, AppSettingRecord> values =
            records.ToDictionary(record => record.SettingKey, StringComparer.Ordinal);

        public int ListCount { get; private set; }

        public Task<IReadOnlyList<AppSettingRecord>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult<IReadOnlyList<AppSettingRecord>>(values.Values.ToList());
        }

        public Task SaveAsync(
            string settingKey,
            string value,
            bool isSecret,
            Guid updatedByAdminUserId,
            CancellationToken cancellationToken = default)
        {
            values[settingKey] = new AppSettingRecord(
                settingKey,
                value,
                isSecret,
                DateTime.UtcNow,
                updatedByAdminUserId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 外部設定がDBより優先され固定項目になる()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "外部設定のお知らせ",
            })
            .Build();
        var store = new FakeStore(Record("DB のお知らせ"));
        var provider = new AppSettingsProvider(configuration, store, Protector);

        var snapshot = await provider.GetAsync();

        Assert.Equal("外部設定のお知らせ", snapshot[AppSettingsProvider.AdminNoticeKey]);
        Assert.Contains(AppSettingsProvider.AdminNoticeKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 外部設定が無ければDBの値を使う()
    {
        var provider = new AppSettingsProvider(
            new ConfigurationBuilder().Build(),
            new FakeStore(Record("DB のお知らせ")),
            Protector);

        var snapshot = await provider.GetAsync();

        Assert.Equal("DB のお知らせ", snapshot[AppSettingsProvider.AdminNoticeKey]);
        Assert.DoesNotContain(AppSettingsProvider.AdminNoticeKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 外部設定もDBも無ければ既定値を使う()
    {
        var provider = new AppSettingsProvider(
            new ConfigurationBuilder().Build(),
            new FakeStore(),
            Protector);

        var snapshot = await provider.GetAsync();

        Assert.Equal(string.Empty, snapshot[AppSettingsProvider.AdminNoticeKey]);
    }

    [Fact]
    public async Task 読み取りはスナップショットを再利用し保存後に読み直す()
    {
        var store = new FakeStore();
        var provider = new AppSettingsProvider(
            new ConfigurationBuilder().Build(),
            store,
            Protector);

        await provider.GetAsync();
        await provider.GetAsync();
        Assert.Equal(1, store.ListCount);

        await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "保存後",
            },
            Guid.NewGuid());

        Assert.Equal(2, store.ListCount);
        Assert.Equal(
            "保存後",
            (await provider.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
        Assert.Equal(2, store.ListCount);
    }

    [Fact]
    public async Task 固定項目は画面から保存してもDBを変えない()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "外部設定",
            })
            .Build();
        var store = new FakeStore(Record("退避値"));
        var provider = new AppSettingsProvider(configuration, store, Protector);

        await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "画面の値",
            },
            Guid.NewGuid());

        Assert.Equal(
            "外部設定",
            (await provider.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
        configuration[AppSettingsProvider.AdminNoticeKey] = null;
        var reloaded = new AppSettingsProvider(configuration, store, Protector);
        Assert.Equal(
            "退避値",
            (await reloaded.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
    }

    private static AppSettingRecord Record(string value) => new(
        AppSettingsProvider.AdminNoticeKey,
        value,
        false,
        DateTime.UtcNow,
        Guid.NewGuid());
}
