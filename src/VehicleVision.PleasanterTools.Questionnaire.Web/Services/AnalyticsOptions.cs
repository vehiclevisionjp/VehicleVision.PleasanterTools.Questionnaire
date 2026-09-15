using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>アクセス解析のサービス（Issue #162）。</summary>
public enum AnalyticsProvider
{
    /// <summary>使わない（既定）。**外部への要求は 1 つも出ない。**</summary>
    None,

    /// <summary>Google アナリティクス 4。</summary>
    Ga4,

    /// <summary>Google タグマネージャー。</summary>
    Gtm,

    /// <summary>Matomo（**自前設置**）。</summary>
    Matomo,

    /// <summary>Plausible（自前設置または plausible.io）。</summary>
    Plausible,
}

/// <summary>アクセス解析の設定。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 設定しなければ、今までどおり回答者の端末から第三者への要求は出ない。
/// </para>
/// <para>
/// ⚠️ **受け取るのは ID と配信元だけ。** 任意のスクリプトを貼れる作りにはしない。
/// **CSP の穴になり、XSS を招く。**
/// </para>
/// <para>
/// 実値は <c>App_Data/Parameters/Analytics.json</c> から読む。
/// </para>
/// </remarks>
public sealed record AnalyticsOptions
{
    /// <summary>使うサービス。</summary>
    public AnalyticsProvider Provider { get; init; } = AnalyticsProvider.None;

    /// <summary>測定 ID・コンテナ ID・サイト ID・ドメイン。**サービスによって意味が違う。**</summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><see cref="AnalyticsProvider.Ga4"/>: 測定 ID（<c>G-XXXXXXX</c>）</item>
    ///   <item><see cref="AnalyticsProvider.Gtm"/>: コンテナ ID（<c>GTM-XXXXXXX</c>）</item>
    ///   <item><see cref="AnalyticsProvider.Matomo"/>: サイト ID（数字）</item>
    ///   <item><see cref="AnalyticsProvider.Plausible"/>: 計測するドメイン</item>
    /// </list>
    /// </remarks>
    public string? SiteId { get; init; }

    /// <summary>スクリプトの配信元。**自前設置のときだけ指定する。**</summary>
    /// <remarks>
    /// Matomo は必須。Plausible は省略すると <c>https://plausible.io</c>。
    /// Google の 2 つは決まった配信元を使うので、ここは見ない。
    /// </remarks>
    public string? ScriptOrigin { get; init; }

    /// <summary>回答者へ「外部サービスへ送っている」ことを知らせるか。**既定は知らせる。**</summary>
    /// <remarks>**伏せない。** 完全匿名を掲げてきた画面で、黙って第三者へ送るのは筋が通らない。</remarks>
    public bool ShowNotice { get; init; } = true;

    /// <summary>設定が使える形になっているか。</summary>
    /// <remarks>
    /// **足りない設定は「無効」と同じ扱いにする。** 半端な状態で読み込ませない。
    /// </remarks>
    public bool IsEnabled =>
        Provider is not AnalyticsProvider.None
        && !string.IsNullOrWhiteSpace(SiteId)
        && ResolvedOrigin() is not null;

    /// <summary>スクリプトの配信元（正規化済み）。**決まらなければ <c>null</c>。**</summary>
    public string? ResolvedOrigin() => Provider switch
    {
        AnalyticsProvider.Ga4 or AnalyticsProvider.Gtm => "https://www.googletagmanager.com",
        AnalyticsProvider.Matomo => NormalizeOrigin(ScriptOrigin),
        AnalyticsProvider.Plausible => NormalizeOrigin(ScriptOrigin) ?? "https://plausible.io",
        _ => null,
    };

    /// <summary>CSP へ足す配信元。**有効なときだけ返す。**</summary>
    /// <remarks>
    /// <para>
    /// Google は**計測の送信先が配信元と別**（`*.google-analytics.com` など）なので、
    /// 送信先も併せて許す必要がある。
    /// </para>
    /// <para>
    /// ⚠️ **`https:` のようには広げない。** 許すのは、選んだサービスの配信元だけ。
    /// </para>
    /// </remarks>
    public ImmutableArray<string> CspSources
    {
        get
        {
            if (!IsEnabled)
            {
                return [];
            }

            var origin = ResolvedOrigin()!;
            return Provider switch
            {
                AnalyticsProvider.Ga4 or AnalyticsProvider.Gtm =>
                [
                    origin,
                    "https://*.google-analytics.com",
                    "https://*.analytics.google.com",
                    "https://*.googletagmanager.com",
                ],
                _ => [origin],
            };
        }
    }

    /// <summary>末尾のスラッシュを落とし、http(s) の絶対 URL だけを通す。</summary>
    /// <remarks>
    /// **設定の書き間違いを黙って通さない。** 相対 URL や別のスキームは無効として扱う。
    /// </remarks>
    private static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>設定から組み立てる。**知らない値は「無効」にする。**</summary>
    /// <remarks>
    /// ⚠️ **ここで例外を投げない。** 解析は無くても回答は取れる。
    /// 設定を書き間違えたせいでアプリが起動しない方が困る
    /// （パスワードの条件のように、無いと危ないものは落とす）。
    /// </remarks>
    public static AnalyticsOptions FromConfiguration(IConfiguration configuration)
    {
        var provider = AnalyticsProvider.None;
        if (configuration["AnalyticsProvider"] is { Length: > 0 } configured
            && (!Enum.TryParse(configured, ignoreCase: true, out provider) || !Enum.IsDefined(provider)))
        {
            provider = AnalyticsProvider.None;
        }

        return new AnalyticsOptions
        {
            Provider = provider,
            SiteId = configuration["AnalyticsSiteId"]?.Trim(),
            ScriptOrigin = configuration["AnalyticsScriptOrigin"]?.Trim(),
            ShowNotice = !bool.TryParse(configuration["AnalyticsShowNotice"], out var showNotice) || showNotice,
        };
    }
}
