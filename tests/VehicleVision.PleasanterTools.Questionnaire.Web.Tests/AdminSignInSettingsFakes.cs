using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>ログイン方式の設定の保存先の代わり。</summary>
internal sealed class FakeAdminSignInSettingStore(string? passwordSignIn = null) : IAdminSignInSettingStore
{
    public AdminSignInSettingValues Values { get; private set; } = new() { PasswordSignIn = passwordSignIn };

    public int Reads { get; private set; }

    public Task<AdminSignInSettingValues> GetAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Values);
    }

    public Task SaveAsync(AdminSignInSettingValues values, CancellationToken cancellationToken = default)
    {
        Values = values;
        return Task.CompletedTask;
    }
}

/// <summary>決まった値を返すログイン方式の設定（方針の試験で、解決の仕組みを切り離すため）。</summary>
internal sealed class FakeAdminSignInSettings(bool passwordSignInEnabled = true)
    : IAdminSignInSettingsProvider
{
    public Task<AdminSignInSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdminSignInSettingsSnapshot(passwordSignInEnabled, Fixed: false, RescueTokenConfigured: false));

    public Task<AdminSignInSaveOutcome> SaveAsync(
        bool passwordSignInEnabled,
        bool ssoEnabled,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
