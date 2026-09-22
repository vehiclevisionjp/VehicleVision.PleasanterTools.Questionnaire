using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面で変更できる設定から Pleasanter の接続設定を解決する。</summary>
public sealed class PleasanterOptionsProvider(
    IAppSettingsProvider appSettings,
    IConfiguration configuration) : IPleasanterOptionsProvider
{
    public async Task<PleasanterOptions> GetAsync(
        CancellationToken cancellationToken = default) =>
        FromSnapshot(
            await appSettings.GetAsync(cancellationToken).ConfigureAwait(false),
            configuration);

    public static PleasanterOptions FromSnapshot(
        AppSettingsSnapshot snapshot,
        IConfiguration configuration)
    {
        var timeout = int.TryParse(
            configuration["QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS"],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : PleasanterOptions.DefaultTimeout;

        return new PleasanterOptions
        {
            BaseUrl = snapshot[AppSettingsProvider.PleasanterBaseUrlKey],
            ApiKey = snapshot[AppSettingsProvider.PleasanterApiKeyKey],
            ApiVersion = decimal.Parse(
                snapshot[AppSettingsProvider.PleasanterApiVersionKey],
                NumberStyles.Number,
                CultureInfo.InvariantCulture),
            Timeout = timeout,
            ApiKeyUserTimeZoneId = snapshot[AppSettingsProvider.PleasanterTimeZoneKey],
        };
    }
}
