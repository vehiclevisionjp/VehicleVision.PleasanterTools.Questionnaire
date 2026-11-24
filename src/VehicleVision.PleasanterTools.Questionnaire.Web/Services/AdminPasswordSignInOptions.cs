namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面の合言葉ログインと緊急時の入口を外部設定から決める。</summary>
/// <remarks>
/// <b><c>Enabled</c> が <c>null</c> なら外部設定では指定されていない</b>（Issue #1056）。
/// その場合は管理画面から保存した値（DB）、それも無ければ既定（隠さない）で動く。
/// 実際に使う値は <see cref="AdminSignInSettingsProvider"/> が解決する。
/// </remarks>
public sealed record AdminPasswordSignInOptions(bool? Enabled, string? RescueToken)
{
    public const string EnabledSetting = "QUESTIONNAIRE_ADMIN_PASSWORD_SIGNIN";
    public const string RescueTokenSetting = "QUESTIONNAIRE_ADMIN_RESCUE_TOKEN";
    public const int MinimumRescueTokenLength = 32;

    /// <summary>外部設定を読み、推測されやすい救済トークンを起動前に拒否する。</summary>
    public static AdminPasswordSignInOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        bool? enabled = null;
        if (configuration[EnabledSetting] is { Length: > 0 } rawEnabled)
        {
            if (!bool.TryParse(rawEnabled.Trim(), out var parsed))
            {
                throw new InvalidOperationException($"{EnabledSetting} must be true or false.");
            }

            enabled = parsed;
        }

        var rescueToken = configuration[RescueTokenSetting];
        if (string.IsNullOrWhiteSpace(rescueToken))
        {
            rescueToken = null;
        }
        else if (rescueToken.Length < MinimumRescueTokenLength)
        {
            // **URL へ載る秘密なので、人が覚えられる短い値を許さない。**
            // 32 文字以上を最低線とし、運用では暗号学的乱数から作る。
            throw new InvalidOperationException(
                $"{RescueTokenSetting} must be at least {MinimumRescueTokenLength} characters.");
        }

        return new AdminPasswordSignInOptions(enabled, rescueToken);
    }
}
