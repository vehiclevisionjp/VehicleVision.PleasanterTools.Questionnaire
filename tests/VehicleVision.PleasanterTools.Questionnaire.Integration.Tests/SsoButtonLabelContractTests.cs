using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// SSO のログインボタンの文言は固定で、設定では変えられない（Issue #1079）。
/// </summary>
/// <remarks>
/// 廃止した環境変数が残っていても、API は表示名を返さず、設定の応答にも項目が無い。
/// 画面は固定の文言（言語ごとの翻訳）を出す。
/// </remarks>
public sealed class SsoButtonLabelContractTests
{
    private static readonly Dictionary<string, string> LegacySettings = new(StringComparer.Ordinal)
    {
        ["QUESTIONNAIRE_SAML_BUTTONLABEL"] = "旧設定の SAML の表示",
        ["QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL"] = "旧設定の Pleasanter の表示",
    };

    [Fact]
    public async Task 旧名の設定が残っていても未認証のセッションは表示名を返さない()
    {
        await using var host = await AdminContractHost.StartAsync(settings: LegacySettings);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/admin/session");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("samlLabel", out _));
        Assert.False(document.RootElement.TryGetProperty("pleasanterSsoLabel", out _));
        Assert.DoesNotContain("旧設定", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/admin/saml/settings")]
    [InlineData("/api/admin/pleasanter-sso/settings")]
    public async Task 旧名の設定が残っていても設定の応答に表示名の項目が無い(string path)
    {
        await using var host = await AdminContractHost.StartAsync(settings: LegacySettings);
        var admin = await host.SeedAdminAsync(withTotp: false);
        using var client = host.CreateClient();
        using var login = await host.LoginAsync(client, admin);
        login.EnsureSuccessStatusCode();

        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("buttonLabel", out _));
        Assert.False(
            document.RootElement.GetProperty("fixedFields").TryGetProperty("buttonLabel", out _));
        Assert.DoesNotContain("旧設定", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 廃止した名前は環境変数と設定ファイルのキーの両方を並べる()
    {
        Assert.Contains("QUESTIONNAIRE_SAML_BUTTONLABEL", RemovedSsoButtonLabelSettings.Names);
        Assert.Contains("QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL", RemovedSsoButtonLabelSettings.Names);
    }
}
