using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using System.Collections.Frozen;
using System.Text.Json;

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

        public int SaveCount { get; private set; }

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
            SaveCount++;
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
        var provider = Create(configuration, new FakeStore(Record("DB のお知らせ")));

        var snapshot = await provider.GetAsync();

        Assert.Equal("外部設定のお知らせ", snapshot[AppSettingsProvider.AdminNoticeKey]);
        Assert.Contains(AppSettingsProvider.AdminNoticeKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 外部設定が無ければDBの値を使う()
    {
        var provider = Create(
            new ConfigurationBuilder().Build(),
            new FakeStore(Record("DB のお知らせ")));

        var snapshot = await provider.GetAsync();

        Assert.Equal("DB のお知らせ", snapshot[AppSettingsProvider.AdminNoticeKey]);
        Assert.DoesNotContain(AppSettingsProvider.AdminNoticeKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 外部設定もDBも無ければ既定値を使う()
    {
        var snapshot = await Create(
            new ConfigurationBuilder().Build(),
            new FakeStore()).GetAsync();

        Assert.Equal(string.Empty, snapshot[AppSettingsProvider.AdminNoticeKey]);
    }

    [Fact]
    public async Task TTL内はスナップショットを再利用し期限後に別インスタンスの保存を読む()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z"));
        var store = new FakeStore();
        var first = Create(new ConfigurationBuilder().Build(), store, time);
        var second = Create(new ConfigurationBuilder().Build(), store, time);

        Assert.Equal(string.Empty, (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
        await second.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "別インスタンスから保存",
            },
            Guid.NewGuid());

        Assert.Equal(string.Empty, (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
        time.Advance(AppSettingsProvider.CacheLifetime);
        Assert.Equal(
            "別インスタンスから保存",
            (await first.GetAsync())[AppSettingsProvider.AdminNoticeKey]);
    }

    [Fact]
    public async Task 自分で保存した後は期限を待たず読み直す()
    {
        var store = new FakeStore();
        var provider = Create(new ConfigurationBuilder().Build(), store);

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
    public async Task 固定項目を変更しようとすると理由を示して拒否する()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "外部設定",
            })
            .Build();
        var store = new FakeStore(Record("退避値"));
        var provider = Create(configuration, store);

        var exception = await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    [AppSettingsProvider.AdminNoticeKey] = "画面の値",
                },
                Guid.NewGuid()));

        Assert.Contains("外部設定で固定されているため", exception.Message);
        configuration[AppSettingsProvider.AdminNoticeKey] = null;
        Assert.Equal(
            "退避値",
            (await Create(configuration, store).GetAsync())[AppSettingsProvider.AdminNoticeKey]);
    }

    [Fact]
    public async Task 固定項目と同じ値を含む保存要求は受け付ける()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "外部設定",
            })
            .Build();
        var provider = Create(configuration, new FakeStore());

        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.AdminNoticeKey] = "外部設定",
            },
            Guid.NewGuid());

        Assert.Equal("外部設定", snapshot[AppSettingsProvider.AdminNoticeKey]);
    }

    [Fact]
    public async Task 保存要求から省略した設定は既存値を保持する()
    {
        var store = new FakeStore(Record("既存値"));
        var provider = Create(new ConfigurationBuilder().Build(), store);

        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>(),
            Guid.NewGuid());

        Assert.Equal("既存値", snapshot[AppSettingsProvider.AdminNoticeKey]);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void 定義が文字列真偽整数を検証できる()
    {
        var text = Definition(AppSettingValueType.String, maximumLength: 3);
        var boolean = Definition(AppSettingValueType.Boolean);
        var integer = Definition(AppSettingValueType.Integer, minimum: 1, maximum: 10);

        Assert.Equal("abc", text.Normalize(" abc "));
        Assert.Throws<AppSettingValidationException>(() => text.Normalize("abcd"));
        Assert.Equal("true", boolean.Normalize("TRUE"));
        Assert.Throws<AppSettingValidationException>(() => boolean.Normalize("yes"));
        Assert.Equal("5", integer.Normalize("05"));
        Assert.Throws<AppSettingValidationException>(() => integer.Normalize("0"));
        Assert.Throws<AppSettingValidationException>(() => integer.Normalize("11"));
    }

    [Fact]
    public void 秘密の設定値は管理画面の応答へ含めない()
    {
        const string secret = "smtp-password-must-not-leak";
        var definition = new AppSettingDefinition(
            "QUESTIONNAIRE_TEST_SECRET",
            AppSettingValueType.String,
            string.Empty,
            "試験用の秘密",
            "Test secret",
            string.Empty,
            string.Empty,
            IsSecret: true);
        var snapshot = new AppSettingsSnapshot(
            [definition],
            new Dictionary<string, string> { [definition.Key] = secret }
                .ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty);

        var response = AdminSettingsEndpoints.Body(snapshot);
        var field = Assert.Single(response.Fields);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Null(field.Value);
        Assert.True(field.HasValue);
        Assert.True(field.IsSecret);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    }

    private static AppSettingsProvider Create(
        IConfiguration configuration,
        IAppSettingStore store,
        TimeProvider? timeProvider = null) =>
        new(configuration, store, Protector, timeProvider ?? TimeProvider.System);

    private static AppSettingDefinition Definition(
        AppSettingValueType type,
        int? minimum = null,
        int? maximum = null,
        int? maximumLength = null) =>
        new("TEST", type, string.Empty, "テスト", "Test", string.Empty, string.Empty,
            Minimum: minimum, Maximum: maximum, MaximumLength: maximumLength);

    private static AppSettingRecord Record(string value) => new(
        AppSettingsProvider.AdminNoticeKey,
        value,
        false,
        DateTime.UtcNow,
        Guid.NewGuid());
}
