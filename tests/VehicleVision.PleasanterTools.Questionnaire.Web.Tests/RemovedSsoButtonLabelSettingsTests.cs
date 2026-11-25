using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>SSO のログインボタンの表示名の設定は廃止した（Issue #1079）。</summary>
public class RemovedSsoButtonLabelSettingsTests
{
    private static IConfiguration Configure(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();

    private static async Task<ListLogger<ParameterFilesReport>> StartAsync(IConfiguration configuration)
    {
        var logger = new ListLogger<ParameterFilesReport>();
        await new ParameterFilesReport(configuration, logger).StartAsync(CancellationToken.None);
        return logger;
    }

    [Fact]
    public async Task 旧名の環境変数があると起動時に警告を1回だけ出す()
    {
        var logger = await StartAsync(Configure(
            ("QUESTIONNAIRE_SAML_BUTTONLABEL", "会社アカウントでログイン"),
            ("QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL", "社内ポータルでログイン")));

        var warning = Assert.Single(
            logger.Messages, message => message.StartsWith("Warning:", StringComparison.Ordinal));
        Assert.Contains("QUESTIONNAIRE_SAML_BUTTONLABEL", warning, StringComparison.Ordinal);
        Assert.Contains("QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL", warning, StringComparison.Ordinal);
        // 値そのものは記録へ出さない
        Assert.DoesNotContain("会社アカウント", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 旧名の設定ファイルのキーがあっても警告する()
    {
        var logger = await StartAsync(Configure(("SamlButtonLabel", "会社アカウントでログイン")));

        var warning = Assert.Single(
            logger.Messages, message => message.StartsWith("Warning:", StringComparison.Ordinal));
        Assert.Contains("SamlButtonLabel", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 旧名が無い又は空なら警告しない()
    {
        var logger = await StartAsync(Configure(
            ("QUESTIONNAIRE_SAML_BUTTONLABEL", ""),
            ("PleasanterSsoButtonLabel", null)));

        Assert.DoesNotContain(
            logger.Messages, message => message.StartsWith("Warning:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 旧名の設定があっても読まず固定項目にもならない()
    {
        var configuration = Configure(("QUESTIONNAIRE_SAML_BUTTONLABEL", "外部設定の表示"));
        var provider = new SamlOptionsProvider(configuration, new FakeSamlStore());

        var snapshot = await provider.GetAsync();

        Assert.DoesNotContain("QUESTIONNAIRE_SAML_BUTTONLABEL", snapshot.FixedKeys);
        Assert.DoesNotContain("外部設定の表示", snapshot.Values.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pleasanterも旧名の設定があっても読まず固定項目にもならない()
    {
        var configuration = Configure(("QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL", "外部設定の表示"));
        var provider = new PleasanterSsoOptionsProvider(
            configuration,
            new FakePleasanterSsoStore(),
            TimeProvider.System,
            new ListLogger<PleasanterSsoOptionsProvider>());

        var snapshot = await provider.GetAsync();

        Assert.DoesNotContain("QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL", snapshot.FixedKeys);
        Assert.DoesNotContain("外部設定の表示", snapshot.Values.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeSamlStore : ISamlSettingStore
    {
        public Task<SamlSettingValues> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SamlSettingValues());

        public Task SaveAsync(SamlSettingValues values, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePleasanterSsoStore : IPleasanterSsoSettingStore
    {
        public Task<PleasanterSsoSettingValues> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PleasanterSsoSettingValues());

        public Task SaveAsync(
            PleasanterSsoSettingValues values,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
