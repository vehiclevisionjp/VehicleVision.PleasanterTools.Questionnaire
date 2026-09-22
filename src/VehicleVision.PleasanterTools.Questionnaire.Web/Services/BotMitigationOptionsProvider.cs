namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面で変更できる bot 対策の実行時設定。</summary>
public sealed record BotMitigationOptions(
    SubmissionGuardOptions SubmissionGuard,
    AltchaOptions Altcha,
    AdminCaptchaOptions AdminCaptcha);

/// <summary>設定スナップショットから bot 対策の実行時設定を解決する。</summary>
public sealed class BotMitigationOptionsProvider(IAppSettingsProvider appSettings)
{
    public const string MitigationEnabledKey = "QUESTIONNAIRE_BOT_MITIGATION";
    public const string SubmitMinimumSecondsKey = "QUESTIONNAIRE_SUBMIT_MIN_SECONDS";
    public const string SubmitTicketHoursKey = "QUESTIONNAIRE_SUBMIT_TICKET_HOURS";
    public const string AltchaEnabledKey = "QUESTIONNAIRE_ALTCHA_ENABLED";
    public const string AltchaMinimumNumberKey = "QUESTIONNAIRE_ALTCHA_MIN_NUMBER";
    public const string AltchaMaximumNumberKey = "QUESTIONNAIRE_ALTCHA_MAX_NUMBER";
    public const string LoginProofOfWorkKey = "QUESTIONNAIRE_LOGIN_PROOF_OF_WORK";

    public async Task<BotMitigationOptions> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await appSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        return new BotMitigationOptions(
            new SubmissionGuardOptions
            {
                Enabled = bool.Parse(settings[MitigationEnabledKey]),
                MinimumElapsed = TimeSpan.FromSeconds(
                    int.Parse(settings[SubmitMinimumSecondsKey], System.Globalization.CultureInfo.InvariantCulture)),
                Lifetime = TimeSpan.FromHours(
                    int.Parse(settings[SubmitTicketHoursKey], System.Globalization.CultureInfo.InvariantCulture)),
            },
            new AltchaOptions
            {
                Enabled = bool.Parse(settings[AltchaEnabledKey]),
                MinimumNumber = int.Parse(
                    settings[AltchaMinimumNumberKey], System.Globalization.CultureInfo.InvariantCulture),
                MaximumNumber = int.Parse(
                    settings[AltchaMaximumNumberKey], System.Globalization.CultureInfo.InvariantCulture),
            },
            new AdminCaptchaOptions(bool.Parse(settings[LoginProofOfWorkKey])));
    }
}
