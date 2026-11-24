using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面のログイン方式の設定（ID/パスワードを出すか）の現在値。</summary>
/// <param name="PasswordSignInEnabled">ID/パスワードのログインを許すと指定されているか（SSO が無効なら無視される前の値）。</param>
/// <param name="Fixed">外部設定（環境変数）で固定されていて、画面から変えられないか。</param>
/// <param name="RescueTokenConfigured">救済トークンが設定済みか（隠しても入口を取り戻せるか）。</param>
public sealed record AdminSignInSettingsSnapshot(
    bool PasswordSignInEnabled,
    bool Fixed,
    bool RescueTokenConfigured);

/// <summary>保存の結果。</summary>
public enum AdminSignInSaveOutcome
{
    Saved,

    /// <summary>外部設定で固定されている。</summary>
    Fixed,

    /// <summary>SAML も Pleasanter のログインも無効なので、隠せない。</summary>
    SsoDisabled,

    /// <summary>救済トークンが未設定なので、隠すと締め出されたときに戻れない。</summary>
    RescueTokenMissing,
}

/// <summary>管理画面のログイン方式の設定を要求ごとに解決する（Issue #1056）。</summary>
public interface IAdminSignInSettingsProvider
{
    Task<AdminSignInSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存する。<b>「隠す」へ変えるときは、SSO が有効で救済トークンが設定済みであること</b>を確かめる。
    /// 画面から押した本人が、その場で入口を失わないため。
    /// </summary>
    Task<AdminSignInSaveOutcome> SaveAsync(
        bool passwordSignInEnabled,
        bool ssoEnabled,
        CancellationToken cancellationToken = default);
}

/// <summary>外部設定 → DB → 既定（隠さない）の順で、ID/パスワードのログインを許すかを決める。</summary>
public sealed class AdminSignInSettingsProvider(
    AdminPasswordSignInOptions options,
    IAdminSignInSettingStore store) : IAdminSignInSettingsProvider
{
    public async Task<AdminSignInSettingsSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var rescue = options.RescueToken is not null;

        // **外部設定があれば DB は読まない。** 要求のたびに通る経路なので、無駄な問い合わせを避ける
        if (options.Enabled is { } external)
        {
            return new AdminSignInSettingsSnapshot(external, Fixed: true, rescue);
        }

        var database = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return new AdminSignInSettingsSnapshot(Parse(database.PasswordSignIn), Fixed: false, rescue);
    }

    public async Task<AdminSignInSaveOutcome> SaveAsync(
        bool passwordSignInEnabled,
        bool ssoEnabled,
        CancellationToken cancellationToken = default)
    {
        if (options.Enabled is not null)
        {
            return AdminSignInSaveOutcome.Fixed;
        }

        if (!passwordSignInEnabled)
        {
            if (!ssoEnabled)
            {
                return AdminSignInSaveOutcome.SsoDisabled;
            }

            if (options.RescueToken is null)
            {
                return AdminSignInSaveOutcome.RescueTokenMissing;
            }
        }

        await store.SaveAsync(
            new AdminSignInSettingValues { PasswordSignIn = passwordSignInEnabled ? "true" : "false" },
            cancellationToken).ConfigureAwait(false);
        return AdminSignInSaveOutcome.Saved;
    }

    /// <summary>**読めない値は「隠さない」へ倒す。** 壊れた値で入口を閉じない。</summary>
    private static bool Parse(string? raw) =>
        !string.Equals(raw?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
