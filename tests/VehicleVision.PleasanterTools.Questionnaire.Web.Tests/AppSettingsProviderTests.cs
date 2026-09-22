using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

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

        public AppSettingRecord? SavedRecord { get; private set; }

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
            SavedRecord = new AppSettingRecord(
                settingKey,
                value,
                isSecret,
                DateTime.UtcNow,
                updatedByAdminUserId);
            values[settingKey] = SavedRecord;
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
    public async Task Pleasanterタイムゾーンはアプリの既定値へフォールバックする()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ParameterFiles.TimeZoneDefaultKey] = "UTC",
            })
            .Build();

        var snapshot = await Create(configuration, new FakeStore()).GetAsync();

        Assert.Equal("UTC", snapshot[AppSettingsProvider.PleasanterTimeZoneKey]);
    }

    [Fact]
    public async Task 外部設定のPleasanterAPIバージョン誤記は既定値へ落とす()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppSettingsProvider.PleasanterApiVersionKey] = "invalid",
            })
            .Build();

        var snapshot = await Create(configuration, new FakeStore()).GetAsync();

        Assert.Equal("1.1", snapshot[AppSettingsProvider.PleasanterApiVersionKey]);
        Assert.Contains(AppSettingsProvider.PleasanterApiVersionKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 値系設定を定義し既定値と上下限を管理画面へ返す()
    {
        var snapshot = await Create(
            new ConfigurationBuilder().Build(),
            new FakeStore()).GetAsync();

        var response = AdminSettingsEndpoints.Body(snapshot);
        var field = Assert.Single(
            response.Fields,
            value => value.Key == "QUESTIONNAIRE_SUBMITS_PER_MIN");

        Assert.Equal("20", field.Value);
        Assert.Equal("20", field.DefaultValue);
        Assert.True(field.IsDefault);
        Assert.Equal(1, field.Minimum);
        Assert.Equal(1000000, field.Maximum);
    }

    [Theory]
    [InlineData("QUESTIONNAIRE_AUDITLOG_RETENTION_DAYS")]
    [InlineData("QUESTIONNAIRE_SEND_PER_MINUTE")]
    [InlineData("QUESTIONNAIRE_BACKLOG_TOTAL")]
    [InlineData("QUESTIONNAIRE_ATTACHMENT_MAXFILECOUNT")]
    [InlineData("QUESTIONNAIRE_SCRIPT_TIMEOUT_MS")]
    [InlineData("QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS")]
    public async Task 値系設定へ0を保存できない(string key)
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        var exception = await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?> { [key] = "0" },
                Guid.NewGuid()));

        Assert.Contains("1 以上", exception.Message);
    }

    [Fact]
    public async Task デッドレターは無期限の既定値を表示するが0へ変更できない()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());
        var snapshot = await provider.GetAsync();

        Assert.Equal("0", snapshot["QUESTIONNAIRE_DEADLETTER_RETENTION_DAYS"]);
        await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    ["QUESTIONNAIRE_DEADLETTER_RETENTION_DAYS"] = "0",
                },
                Guid.NewGuid()));
    }

    [Fact]
    public async Task 許可拡張子を正規化し配布できない形式を拒否する()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                ["QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS"] = "pdf, .PNG, PDF",
                ["QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS"] = "pdf, .jpg",
            },
            Guid.NewGuid());

        Assert.Equal(
            ".pdf, .png",
            snapshot["QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS"]);
        await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    ["QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS"] = ".pdf, .svg",
                },
                Guid.NewGuid()));
    }

    [Fact]
    public void 監視サービスへ渡したスナップショットを登録先へ直ちに反映する()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = Create(configuration, new FakeStore());
        var monitor = new AppSettingsMonitor(
            provider,
            configuration,
            new DatabaseStartupState(),
            NullLogger<AppSettingsMonitor>.Instance,
            TimeProvider.System);
        var snapshot = AppSettingsProvider.InitialSnapshot(configuration);
        var observed = string.Empty;

        monitor.Register(current =>
            observed = current["QUESTIONNAIRE_SUBMITS_PER_MIN"]);
        var changedValues = snapshot.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        changedValues["QUESTIONNAIRE_SUBMITS_PER_MIN"] = "42";
        monitor.Apply(snapshot with
        {
            Values = changedValues.ToFrozenDictionary(StringComparer.Ordinal),
        });

        Assert.Equal("42", observed);
        Assert.Equal(42, monitor.GetInt32("QUESTIONNAIRE_SUBMITS_PER_MIN"));
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
    public async Task 埋め込み設定を正規化して不正なホスト源を拒否する()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [EmbedParentOptions.AllowedParentsKey] =
                    "www.example.com, *.example.net, WWW.EXAMPLE.COM",
                [EmbedOptions.AllowedHostsKey] = "media.example.com:8443",
            },
            Guid.NewGuid());

        Assert.Equal(
            "www.example.com, *.example.net",
            snapshot[EmbedParentOptions.AllowedParentsKey]);
        Assert.Equal("media.example.com:8443", snapshot[EmbedOptions.AllowedHostsKey]);
        await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    [EmbedOptions.AllowedHostsKey] = "www.example.com' https:",
                },
                Guid.NewGuid()));
    }

    [Fact]
    public async Task 埋め込みの外部設定はDBより優先され固定項目になる()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmbedParentOptions.AllowedParentsKey] = "portal.example.com",
            })
            .Build();
        var provider = Create(configuration, new FakeStore(new AppSettingRecord(
            EmbedParentOptions.AllowedParentsKey,
            "db.example.com",
            false,
            DateTime.UtcNow,
            Guid.NewGuid())));

        var snapshot = await provider.GetAsync();

        Assert.Equal("portal.example.com", snapshot[EmbedParentOptions.AllowedParentsKey]);
        Assert.Contains(EmbedParentOptions.AllowedParentsKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task Pleasanter接続設定4件を管理できる()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.PleasanterBaseUrlKey] = "https://pleasanter.example.test/",
                [AppSettingsProvider.PleasanterApiVersionKey] = "1.10",
                [AppSettingsProvider.PleasanterTimeZoneKey] = "Asia/Tokyo",
                [AppSettingsProvider.PleasanterApiKeyKey] = "secret-api-key",
            },
            Guid.NewGuid());

        Assert.Equal(
            "https://pleasanter.example.test",
            snapshot[AppSettingsProvider.PleasanterBaseUrlKey]);
        Assert.Equal("1.10", snapshot[AppSettingsProvider.PleasanterApiVersionKey]);
        Assert.Equal("Asia/Tokyo", snapshot[AppSettingsProvider.PleasanterTimeZoneKey]);
        Assert.Equal("secret-api-key", snapshot[AppSettingsProvider.PleasanterApiKeyKey]);
        Assert.True(snapshot.Definitions.Single(
            definition => definition.Key == AppSettingsProvider.PleasanterApiKeyKey).IsSecret);
    }

    [Fact]
    public async Task PleasanterAPIキーは保護して保存し空欄では既存値を消さない()
    {
        var store = new FakeStore();
        var provider = Create(new ConfigurationBuilder().Build(), store);

        await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.PleasanterApiKeyKey] = "secret-api-key",
            },
            Guid.NewGuid());

        Assert.NotNull(store.SavedRecord);
        Assert.True(store.SavedRecord.IsSecret);
        Assert.DoesNotContain("secret-api-key", store.SavedRecord.Value, StringComparison.Ordinal);
        Assert.Equal("secret-api-key", Protector.Unprotect(store.SavedRecord.Value));

        var saveCount = store.SaveCount;
        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.PleasanterApiKeyKey] = " ",
            },
            Guid.NewGuid());

        Assert.Equal(saveCount, store.SaveCount);
        Assert.Equal("secret-api-key", snapshot[AppSettingsProvider.PleasanterApiKeyKey]);
    }

    [Theory]
    [InlineData("ftp://pleasanter.example.test")]
    [InlineData("relative")]
    [InlineData("https://pleasanter.example.test/?key=value")]
    [InlineData("https://user:password@pleasanter.example.test")]
    public async Task Pleasanterの不正なURLを拒否する(string value)
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    [AppSettingsProvider.PleasanterBaseUrlKey] = value,
                },
                Guid.NewGuid()));
    }

    [Fact]
    public async Task bot対策の外部設定は従来のoffも含めて正規化する()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BotMitigationOptionsProvider.MitigationEnabledKey] = "off",
            })
            .Build();

        var snapshot = await Create(configuration, new FakeStore()).GetAsync();

        Assert.Equal("false", snapshot[BotMitigationOptionsProvider.MitigationEnabledKey]);
        Assert.Contains(BotMitigationOptionsProvider.MitigationEnabledKey, snapshot.FixedKeys);
    }

    /// <summary>検証環境は proof-of-work を軽くするため、既定より低い値を渡す（Issue #383）。</summary>
    [Fact]
    public async Task 外部設定は下限を下回っていてもそのまま使う()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BotMitigationOptionsProvider.AltchaMinimumNumberKey] = "1000",
                [BotMitigationOptionsProvider.AltchaMaximumNumberKey] = "5000",
            })
            .Build();

        var snapshot = await Create(configuration, new FakeStore()).GetAsync();

        Assert.Equal("1000", snapshot[BotMitigationOptionsProvider.AltchaMinimumNumberKey]);
        Assert.Equal("5000", snapshot[BotMitigationOptionsProvider.AltchaMaximumNumberKey]);
    }

    /// <summary>⚠️ 起動を止めると、設定を直す手立てごと失う（Issue #383）。</summary>
    [Fact]
    public async Task 書式が壊れた外部設定は既定値へ落として起動を続ける()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BotMitigationOptionsProvider.AltchaMinimumNumberKey] = "たくさん",
            })
            .Build();

        var snapshot = await Create(configuration, new FakeStore()).GetAsync();

        Assert.Equal("50000", snapshot[BotMitigationOptionsProvider.AltchaMinimumNumberKey]);
    }

    /// <summary>画面からは今までどおり下限を割れない（Issue #383）。</summary>
    [Fact]
    public async Task 画面からは下限を下回る値を保存できない()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        await Assert.ThrowsAsync<AppSettingValidationException>(() =>
            provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    [BotMitigationOptionsProvider.AltchaMinimumNumberKey] = "1000",
                },
                Guid.NewGuid()));
    }

    [Fact]
    public async Task bot対策の数値設定は安全な範囲外を拒否する()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());

        await Assert.ThrowsAsync<AppSettingValidationException>(() => provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [BotMitigationOptionsProvider.SubmitMinimumSecondsKey] = "0",
            },
            Guid.NewGuid()));
        await Assert.ThrowsAsync<AppSettingValidationException>(() => provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [BotMitigationOptionsProvider.AltchaMaximumNumberKey] = "500001",
            },
            Guid.NewGuid()));
    }

    [Fact]
    public async Task bot対策の管理対象は安全な既定値を持つ()
    {
        var snapshot = await Create(new ConfigurationBuilder().Build(), new FakeStore()).GetAsync();

        Assert.Equal("true", snapshot[BotMitigationOptionsProvider.MitigationEnabledKey]);
        Assert.Equal("3", snapshot[BotMitigationOptionsProvider.SubmitMinimumSecondsKey]);
        Assert.Equal("24", snapshot[BotMitigationOptionsProvider.SubmitTicketHoursKey]);
        Assert.Equal("true", snapshot[BotMitigationOptionsProvider.AltchaEnabledKey]);
        Assert.Equal("50000", snapshot[BotMitigationOptionsProvider.AltchaMinimumNumberKey]);
        Assert.Equal("150000", snapshot[BotMitigationOptionsProvider.AltchaMaximumNumberKey]);
        Assert.Equal("false", snapshot[BotMitigationOptionsProvider.LoginProofOfWorkKey]);
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

    [Fact]
    public async Task Pleasanter接続の未設定状態を管理画面へ返す()
    {
        var snapshot = await Create(new ConfigurationBuilder().Build(), new FakeStore()).GetAsync();

        var response = AdminSettingsEndpoints.Body(snapshot);

        Assert.False(response.IsPleasanterConfigured);
    }

    [Fact]
    public async Task Pleasanter接続が設定済みなら未設定として扱わない()
    {
        var provider = Create(new ConfigurationBuilder().Build(), new FakeStore());
        var snapshot = await provider.SaveAsync(
            new Dictionary<string, string?>
            {
                [AppSettingsProvider.PleasanterBaseUrlKey] = "https://pleasanter.example.test",
                [AppSettingsProvider.PleasanterApiKeyKey] = "secret-api-key",
            },
            Guid.NewGuid());

        Assert.Empty(PleasanterConfigurationReport.MissingKeys(snapshot));
        Assert.True(AdminSettingsEndpoints.Body(snapshot).IsPleasanterConfigured);
    }

    private static AppSettingsProvider Create(
        IConfiguration configuration,
        IAppSettingStore store,
        TimeProvider? timeProvider = null) =>
        new(
            configuration,
            store,
            Protector,
            timeProvider ?? TimeProvider.System,
            NullLogger<AppSettingsProvider>.Instance);

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
