namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Pleasanter 接続設定の未設定を起動時に記録する。</summary>
public static class PleasanterConfigurationReport
{
    public static async Task ReportAsync(
        IAppSettingsProvider settingsProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var missing = MissingKeys(settings);
        if (missing.Count == 0)
        {
            return;
        }

        // **英語で書く。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）
        logger.LogWarning(
            "Pleasanter delivery is not configured. Missing settings: {MissingSettings}. "
            + "The application will accept responses, but delivery to Pleasanter will remain queued "
            + "until the connection is configured in the administration screen.",
            string.Join(", ", missing));
    }

    public static IReadOnlyList<string> MissingKeys(AppSettingsSnapshot settings)
    {
        var missing = new List<string>();
        if (!settings.Values.TryGetValue(
                AppSettingsProvider.PleasanterBaseUrlKey,
                out var baseUrl)
            || string.IsNullOrWhiteSpace(baseUrl))
        {
            missing.Add(AppSettingsProvider.PleasanterBaseUrlKey);
        }

        if (!settings.Values.TryGetValue(
                AppSettingsProvider.PleasanterApiKeyKey,
                out var apiKey)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            missing.Add(AppSettingsProvider.PleasanterApiKeyKey);
        }

        return missing;
    }
}
