namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面の認証へ proof-of-work を課す設定。</summary>
public sealed record AdminCaptchaOptions(bool Enabled)
{
    public const string EnabledSetting = "QUESTIONNAIRE_LOGIN_PROOF_OF_WORK";

    /// <summary>環境変数の明示値を読む。**未設定では無効。**</summary>
    public static AdminCaptchaOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[EnabledSetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AdminCaptchaOptions(false);
        }

        return bool.TryParse(raw.Trim(), out var enabled)
            ? new AdminCaptchaOptions(enabled)
            : throw new InvalidOperationException(
                $"{EnabledSetting} must be true or false.");
    }
}
