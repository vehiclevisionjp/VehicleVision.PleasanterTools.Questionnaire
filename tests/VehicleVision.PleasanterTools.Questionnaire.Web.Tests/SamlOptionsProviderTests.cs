using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>SAML 設定の優先順位と保存（Issue #254）。</summary>
public class SamlOptionsProviderTests
{
    private sealed class FakeStore(SamlSettingValues values) : ISamlSettingStore
    {
        public SamlSettingValues Values { get; private set; } = values;

        public Task<SamlSettingValues> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Values);

        public Task SaveAsync(
            SamlSettingValues values,
            CancellationToken cancellationToken = default)
        {
            Values = values;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task 外部設定がDBより優先され固定項目になる()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SamlOptions.EnabledKey] = "false",
                [SamlOptions.ButtonLabelKey] = "外部設定の表示",
            })
            .Build();
        var store = new FakeStore(new SamlSettingValues
        {
            Enabled = "true",
            ButtonLabel = "DB の表示",
            UnknownUser = "Register",
        });
        var provider = new SamlOptionsProvider(configuration, store);

        var snapshot = await provider.GetAsync();

        Assert.False(snapshot.Options.Enabled);
        Assert.Equal("外部設定の表示", snapshot.Values.ButtonLabel);
        Assert.Equal("Register", snapshot.Values.UnknownUser);
        Assert.Contains(SamlOptions.EnabledKey, snapshot.FixedKeys);
        Assert.Contains(SamlOptions.ButtonLabelKey, snapshot.FixedKeys);
        Assert.DoesNotContain(SamlOptions.UnknownUserKey, snapshot.FixedKeys);
    }

    [Fact]
    public async Task 固定項目は画面から保存してもDBの退避値を変えない()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SamlOptions.EnabledKey] = "false",
                [SamlOptions.ButtonLabelKey] = "外部設定の表示",
            })
            .Build();
        var store = new FakeStore(new SamlSettingValues
        {
            Enabled = "true",
            ButtonLabel = "退避している表示",
            UnknownUser = "Reject",
        });
        var provider = new SamlOptionsProvider(configuration, store);

        await provider.SaveAsync(new SamlSettingValues
        {
            Enabled = "false",
            ButtonLabel = "画面からの表示",
            UnknownUser = "Register",
        });

        Assert.Equal("true", store.Values.Enabled);
        Assert.Equal("退避している表示", store.Values.ButtonLabel);
        Assert.Equal("Register", store.Values.UnknownUser);
    }

    [Fact]
    public async Task 未設定項目は既定値へ落ちる()
    {
        var provider = new SamlOptionsProvider(
            new ConfigurationBuilder().Build(),
            new FakeStore(new SamlSettingValues()));

        var snapshot = await provider.GetAsync();

        Assert.False(snapshot.Options.Enabled);
        Assert.Equal("false", snapshot.Values.Enabled);
        Assert.Equal("Reject", snapshot.Values.UnknownUser);
        Assert.Equal("Editor", snapshot.Values.RegisterRole);
        Assert.Equal("NameId", snapshot.Values.LoginIdSource);
    }
}
