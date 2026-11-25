namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 廃止した「SSO のログインボタンの表示名」の設定を探す（Issue #1079）。
/// </summary>
/// <remarks>
/// <para>
/// ボタンの文言は画面の言語ごとの固定の文言にした（SAML は「SSO でログイン」、
/// Pleasanter は「Pleasanter でログイン」）。**旧名は読まない。**
/// 指定が残っていても動作は変わらないので、運用者が気付けるように、
/// 起動時に無視した旨を記録へ出す。旧名のフォローはしない方針（2026-11-11）。
/// </para>
/// <para>
/// 環境変数と、設定ファイルのキー（<c>Authentication.json</c> と
/// <c>Authentication.local.json</c>）を見る。値が空のものは指定とみなさない。
/// </para>
/// </remarks>
public static class RemovedSsoButtonLabelSettings
{
    /// <summary>廃止した名前。環境変数と設定ファイルのキーの両方を並べる。</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        SamlOptions.ButtonLabelKey,
        PleasanterSsoOptions.ButtonLabelKey,
        "SamlButtonLabel",
        "PleasanterSsoButtonLabel",
    ];

    /// <summary>指定が残っている廃止済みの名前。</summary>
    public static IReadOnlyList<string> Find(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return [.. Names.Where(name => !string.IsNullOrWhiteSpace(configuration[name]))];
    }
}
