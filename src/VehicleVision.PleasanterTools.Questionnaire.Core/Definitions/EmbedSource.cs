using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>埋め込みの出し方。</summary>
public enum EmbedKind
{
    /// <summary>画像。<c>img</c> として出す。</summary>
    Image,

    /// <summary>外部ページ・動画。<c>iframe</c> として出す。</summary>
    /// <remarks>
    /// **動画もこれ。** 動画共有サービスの埋め込みは <c>iframe</c> で配られる。
    /// </remarks>
    Frame,
}

/// <summary>設問の間へ差し込む埋め込み 1 つ（Issue #104 / #107）。</summary>
/// <param name="Kind">出し方。</param>
/// <param name="Url">
/// 埋め込み先。**絶対 URL の <c>https:</c> のみ**、かつ
/// **運用側が許した配信元**（<see cref="EmbedPolicy"/>）。
/// </param>
/// <param name="AlternativeText">
/// 代わりに読む文字列。**画像では <c>alt</c>、外部ページでは <c>title</c>。**
/// **読み上げに要る。** 埋め込みは見えない人には何も伝えない。
/// </param>
/// <param name="AspectRatio">
/// 高さの取り方（幅に対する比）。**外部ページで使う。**
/// 既定は 16:9。**入れておかないと、読み込むまで高さが決まらず画面が飛び跳ねる。**
/// </param>
public sealed record EmbedSource(
    EmbedKind Kind,
    string Url,
    LocalizedText? AlternativeText = null,
    double AspectRatio = EmbedSource.DefaultAspectRatio)
{
    /// <summary>既定の縦横比（16:9）。</summary>
    public const double DefaultAspectRatio = 16d / 9d;

    /// <summary>受け付ける縦横比の下限。</summary>
    public const double MinimumAspectRatio = 0.2;

    /// <summary>受け付ける縦横比の上限。</summary>
    public const double MaximumAspectRatio = 5.0;

    /// <summary>画面へ出す前に、収まる値へ落とす。</summary>
    public EmbedSource Normalized() => this with
    {
        AspectRatio = double.IsFinite(AspectRatio)
            ? Math.Clamp(AspectRatio, MinimumAspectRatio, MaximumAspectRatio)
            : DefaultAspectRatio,
    };
}

/// <summary>埋め込んでよい配信元を決める（Issue #107）。</summary>
/// <remarks>
/// <para>
/// **管理者が任意のホストを書けると、実質 <c>frame-src https:</c> と変わらない。**
/// 許すホストは**運用側が設定で決め、そこに無いホストは保存の時点で弾く。**
/// **既定は空。** 何も設定しなければ一切埋め込めない（いまと同じ）。
/// </para>
/// <para>
/// **書き方は CSP のホスト源に揃える。** <c>*.example.net</c> は
/// <c>example.net</c> 自体には一致しない。**CSP が通す範囲と、
/// 保存を許す範囲を違えない**ためで、片方だけ通ると
/// 「保存できたのに出ない」という分かりにくい壊れ方をする。
/// </para>
/// </remarks>
public static class EmbedPolicy
{
    /// <summary>すべてのサブドメインを表す接頭辞。</summary>
    private const string SubdomainPrefix = "*.";

    /// <summary>埋め込み先として受け付けてよい URL か。</summary>
    /// <param name="url">埋め込み先。</param>
    /// <param name="allowedHosts">運用側が許したホスト。</param>
    /// <remarks>
    /// **<c>https:</c> 以外は通さない。** 途中の経路で差し替えられる埋め込みを
    /// 回答画面へ載せない。**利用者情報の付いた URL を作らせない**ため、
    /// 利用者情報（<c>user:pass@</c>）が入っていても通さない。
    /// </remarks>
    public static bool IsAllowed(string? url, IReadOnlyCollection<string>? allowedHosts)
    {
        if (allowedHosts is null || allowedHosts.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return allowedHosts.Any(pattern => Matches(uri.Host, pattern));
    }

    /// <summary>1 つのホストが、設定の書き方に当てはまるか。</summary>
    public static bool Matches(string host, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        pattern = pattern.Trim();

        if (!pattern.StartsWith(SubdomainPrefix, StringComparison.Ordinal))
        {
            return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var suffix = pattern[SubdomainPrefix.Length..];

        // **`*.` だけ書かれても全部は許さない。** 設定の書き間違いで
        // すべてのホストが通ると、絞った意味が無くなる
        if (suffix.Length == 0)
        {
            return false;
        }

        // **`*.example.net` は `example.net` 自体に一致しない**（CSP と同じ）。
        // 一致するのは「1 つ以上のラベルが前に付いたもの」だけ
        return host.Length > suffix.Length + 1
            && host.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>設定の書き方を、CSP のホスト源へ直す。</summary>
    /// <remarks>
    /// **CSP に書けない形は落とす。** 空白や引用符が混ざったまま出すと、
    /// **ヘッダの区切りが壊れて指定そのものが効かなくなる。**
    /// </remarks>
    public static ImmutableArray<string> ToCspSources(IEnumerable<string>? allowedHosts)
    {
        if (allowedHosts is null)
        {
            return [];
        }

        return allowedHosts
            .Select(host => host?.Trim() ?? string.Empty)
            .Where(IsWritableInCsp)
            .Select(host => "https://" + host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    /// <summary>CSP のヘッダへそのまま書ける形か。</summary>
    private static bool IsWritableInCsp(string source)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var body = source.StartsWith(SubdomainPrefix, StringComparison.Ordinal)
            ? source[SubdomainPrefix.Length..]
            : source;

        var colon = body.LastIndexOf(':');
        var host = colon < 0 ? body : body[..colon];
        if (colon >= 0
            && (body.AsSpan(0, colon).Contains(':')
                || !int.TryParse(body.AsSpan((colon + 1)..), out var port)
                || port is < 1 or > 65535))
        {
            return false;
        }

        if (host.Length is 0 or > 253)
        {
            return false;
        }

        return host.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0])
            && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
