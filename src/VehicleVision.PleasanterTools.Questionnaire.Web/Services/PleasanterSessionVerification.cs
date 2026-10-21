using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Core.Web の本人確認（<c>PleasanterSessionVerifier</c>）へ渡す、本アプリの値（Issue #706）。</summary>
/// <remarks>
/// ⚠️ **値は Core.Web へ置き換える前の本アプリの検証器のものをそのまま使う。**
/// <list type="bullet">
/// <item><see cref="SessionProbeKey"/> を変えると、Pleasanter のセッションに別の鍵が書かれる。</item>
/// <item><see cref="LoggerCategory"/> を変えると、カテゴリでログを絞っている設定が効かなくなる。</item>
/// </list>
/// </remarks>
public static class PleasanterSessionVerification
{
    /// <summary>代わりの経路で Pleasanter の <c>/api/sessions/set</c> に書く鍵。</summary>
    public const string SessionProbeKey = "VehicleVision.Questionnaire.SsoProbe";

    /// <summary>ログのカテゴリ。置き換える前の検証器の型の完全名。</summary>
    public const string LoggerCategory = "VehicleVision.PleasanterTools.Questionnaire.Web.Services.PleasanterSessionVerifier";

    /// <summary>Core.Web の検証器を登録する。本アプリの Pleasanter 接続設定の読み出し口も一緒に登録する。</summary>
    /// <remarks>
    /// HttpClient（cookie を覚えない・転送を追わない・時間切れは無限で、要求ごとに設定の値で掛ける）は
    /// Core.Web が登録する。**<see cref="IPleasanterOptionsProvider"/> は呼び出し側で登録しておくこと。**
    /// </remarks>
    public static IServiceCollection AddQuestionnairePleasanterSessionVerifier(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPleasanterApiConnectionSource, PleasanterApiConnectionSource>();
        return services.AddPleasanterSessionVerifier(Settings);
    }

    /// <summary>Core.Web へ渡す値。</summary>
    public static PleasanterSessionVerifierSettings Settings { get; } = new()
    {
        SessionProbeKey = SessionProbeKey,
        BaseUrlSettingName = AppSettingsProvider.PleasanterBaseUrlKey,
        ApiKeySettingName = AppSettingsProvider.PleasanterApiKeyKey,
        LoggerCategory = LoggerCategory,
    };
}
