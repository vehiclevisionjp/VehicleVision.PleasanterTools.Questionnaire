using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Primitives;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理 API の状態を変える要求が、同一オリジンの画面から来たものかを判定する（Issue #907）。</summary>
/// <remarks>
/// <para>
/// **<c>SameSite=Strict</c> だけでは防げない。** 「サイト」は登録可能なドメイン単位なので、
/// Pleasanter（<c>pleasanter.example.com</c>）と本アプリ（<c>questionnaire.example.com</c>）のように
/// サブドメインで併設すると同一サイトになり、Pleasanter 側のページから送った <c>POST</c> に
/// 管理者の cookie が付く（本文なしの停止・再開・失効などがそのまま通る）。
/// そこで、**ブラウザが付けるヘッダでオリジンを確かめる。**
/// </para>
/// <para>
/// **判定の順序**（対象は <c>/api/admin/**</c> の <c>GET</c>・<c>HEAD</c>・<c>OPTIONS</c>・<c>TRACE</c> 以外）。
/// </para>
/// <list type="number">
/// <item><description>
/// <c>Sec-Fetch-Site</c> がある: <c>same-origin</c> か <c>none</c> だけ通す。
/// <c>same-site</c>・<c>cross-site</c>・未知の値・複数の値は拒む。
/// </description></item>
/// <item><description>
/// 無く、<c>Origin</c> がある: 本アプリの公開オリジン（<c>Scheme://Host</c>。
/// <c>UseForwardedHeaders</c> が決めた値）と一致するときだけ通す。<c>Origin: null</c> も拒む。
/// </description></item>
/// <item><description>
/// どちらも無い: **通す。** ブラウザはクロスオリジンの <c>POST</c> に必ず <c>Origin</c> を付ける
/// （<c>Sec-Fetch-Site</c> を知らない古いブラウザも同じ）ので、両方無い要求はブラウザの CSRF の経路にならない。
/// curl などの非ブラウザからの呼び出しを壊さないための判断。
/// </description></item>
/// </list>
/// <para>
/// **例外は <see cref="CrossSitePostPaths"/> に、パスを明示して置く。** 既定は拒否。
/// 別サイトからの <c>POST</c> を意図する経路（IdP からの SAML）だけで、それぞれ
/// 署名・対応する要求の照合など、別の仕組みで守られている。
/// </para>
/// <para>
/// ⚠️ **公開オリジンは設定から取らない。** 本アプリは公開 URL の設定を持たず、SAML の受け口の URL も
/// 要求の配信元から作る（<see cref="Endpoints.AdminSamlEndpoints"/>）。同じ流儀で
/// <c>UseForwardedHeaders</c>（<c>X-Forwarded-For</c>・<c>X-Forwarded-Proto</c>。信頼するネットワークは
/// 運用者が指定）が決めた <c>Request.Scheme</c> と、受け取った <c>Host</c> を使う。
/// ブラウザは <c>Host</c> を偽れず、攻撃者が足せるのは <c>Origin</c> だけなので、
/// 比較の相手を <c>Host</c> にしても CSRF の防御は弱まらない。
/// <c>Origin</c> にパスは含まれないので、サブパス（PathBase）は比較に入らない。
/// </para>
/// </remarks>
public static class AdminCsrfGuard
{
    /// <summary>守る範囲の先頭（PathBase を剥がした後の Path）。</summary>
    public const string ProtectedPrefix = "/api/admin";

    /// <summary>拒否したときに返す <c>code</c>。内部の理由は返さない。</summary>
    public const string RejectedCode = "cross-site-request-rejected";

    /// <summary>
    /// **別サイトからの <c>POST</c> を意図する経路。** ここに無い経路は、別サイトからの状態変更を拒む。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// <c>/api/admin/saml/acs</c>: IdP の画面から、利用者のブラウザが <c>POST</c> する（HTTP-POST バインディング）。
    /// 署名・宛先・期限・こちらが出した要求との対応で守られる。
    /// </description></item>
    /// <item><description>
    /// <c>/api/admin/saml/slo</c>: IdP からの単一ログアウト（<c>POST</c> バインディングがあり得る）。
    /// 署名を確かめ、落とすのはこちらの cookie だけ。
    /// </description></item>
    /// </list>
    /// Pleasanter のログインの戻り（<c>/api/admin/pleasanter-sso/check</c>）は**別サイトからの <c>POST</c> ではない**。
    /// 本アプリの画面が同一オリジンから <c>fetch</c> するので、例外にしない。
    /// </remarks>
    public static IReadOnlyList<string> CrossSitePostPaths { get; } =
    [
        "/api/admin/saml/acs",
        "/api/admin/saml/slo",
    ];

    /// <summary>この要求を検証の対象にするか。</summary>
    public static bool IsProtected(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsSafeMethod(request.Method)
            || !request.Path.StartsWithSegments(ProtectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsCrossSitePostPath(request.Path);
    }

    /// <summary>
    /// 要求を判定する。<b>拒むとき <paramref name="reason"/> に、ログへ出してよい値の種別だけを返す。</b>
    /// </summary>
    public static bool IsAllowed(HttpRequest request, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        reason = null;

        if (!IsProtected(request))
        {
            return true;
        }

        var site = request.Headers["Sec-Fetch-Site"];
        if (site.Count > 0)
        {
            if (site.Count == 1
                && (string.Equals(site[0], "same-origin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(site[0], "none", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            reason = $"sec-fetch-site={LogValueOf(site)}";
            return false;
        }

        var origin = request.Headers.Origin;
        if (origin.Count == 0)
        {
            // **ブラウザの CSRF の経路にならない**（クラスの説明）
            return true;
        }

        if (origin.Count == 1 && MatchesPublicOrigin(origin[0], request))
        {
            return true;
        }

        reason = "origin=mismatch";
        return false;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    private static bool IsCrossSitePostPath(PathString path)
    {
        // 経路の照合は大文字小文字と末尾の `/` を区別しないので、ここも合わせる
        var normalized = (path.Value ?? string.Empty).TrimEnd('/');
        foreach (var allowed in CrossSitePostPaths)
        {
            if (string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>Origin</c> が、この要求の公開オリジンと一致するか。</summary>
    private static bool MatchesPublicOrigin(string? origin, HttpRequest request)
    {
        if (string.IsNullOrEmpty(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            // `Origin` はオリジンだけ。パス・クエリ・ユーザー情報が付いたものは別物として扱う
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || origin.EndsWith('/'))
        {
            return false;
        }

        if (!request.Host.HasValue)
        {
            return false;
        }

        var scheme = request.Scheme;
        var port = request.Host.Port
            ?? (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        return string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == port;
    }

    /// <summary>
    /// <c>Sec-Fetch-Site</c> の値をログへ出せる形にする。
    /// <b>要求側が自由に書ける値なので、既知の語以外はそのまま出さない</b>（ログの汚染を避ける）。
    /// </summary>
    private static string LogValueOf(StringValues values)
    {
        if (values.Count != 1)
        {
            return "multiple";
        }

        var value = values[0];
        foreach (var known in new[] { "same-site", "cross-site", "same-origin", "none" })
        {
            if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return "unknown";
    }
}
