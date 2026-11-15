namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>
/// トップページ（<c>/</c>）への直接アクセスを、設定した URL へ 302 で案内する（Issue #967）。
/// </summary>
/// <remarks>
/// <para>
/// **未設定なら今までどおり、入口の HTML を返す。** 対象は GET・HEAD の <c>/</c> だけで、
/// <c>/index.html</c>・<c>/f/…</c>・管理画面・API・<c>/healthz</c> は変えない。
/// </para>
/// <para>
/// **宛先は、設定した人だけが決める。** 要求の値（ヘッダ・問い合わせ文字列）は宛先へ一切混ぜない。
/// これを守らないと、オープンリダイレクトになる。
/// </para>
/// <para>
/// **宛先はサイト内のパス（<c>/</c> 始まり）か、<c>http</c>・<c>https</c> の絶対 URL。**
/// サイト内のパスはサブパス配置のとき、要求の PathBase を先頭へ足す（管理画面の戻り先と同じ流儀）。
/// 次は起動時に止める。プロトコル相対（<c>//host</c>・<c>/\host</c>）、他のスキーム（<c>javascript:</c> など）、
/// 空白・制御文字・非 ASCII、ユーザー情報つき（<c>user:pass@host</c>）、URL として読めない値。
/// </para>
/// <para>
/// **外部設定だけで決める。** 起動時に 1 度だけ読み、画面からは変えない（<see cref="AdminPathOptions"/> と同じ扱い）。
/// </para>
/// </remarks>
public sealed record RootRedirectOptions(string? Target)
{
    /// <summary>設定の鍵。</summary>
    public const string Setting = "QUESTIONNAIRE_ROOT_REDIRECT_URL";

    /// <summary>宛先の最大長。</summary>
    public const int MaximumLength = 2048;

    /// <summary>リダイレクトしない（今までどおり入口の HTML を返す）。</summary>
    public static RootRedirectOptions None { get; } = new((string?)null);

    /// <summary>リダイレクトを使うか。</summary>
    public bool IsConfigured => Target is not null;

    /// <summary>外部設定を読み、宛先として使えることを確かめる。</summary>
    public static RootRedirectOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Setting]);
    }

    /// <summary>設定値を検証する。未設定・空は使わない。**書き間違いは起動時に止める。**</summary>
    /// <remarks>
    /// 黙って捨てると、案内したつもりで入口の HTML が出続け、原因を追えない。
    /// **例外の文言は英語。** Azure の Kudu の Debug console で日本語が化ける（Issue #225）。
    /// **値そのものは文言へ含めない**（宛先の問い合わせ文字列に秘密を置かれてもログへ出さないため）。
    /// </remarks>
    public static RootRedirectOptions Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        if (!IsAcceptable(value))
        {
            throw new InvalidOperationException(
                $"{Setting} must be a site-internal path that starts with a single '/', "
                + "or an absolute http or https URL with a host and no user information "
                + $"(printable ASCII only, no whitespace or control characters, maximum length {MaximumLength}). "
                + "Protocol-relative URLs and other schemes are not allowed.");
        }

        return new RootRedirectOptions(value);
    }

    /// <summary>リダイレクト先を組み立てる。サイト内のパスには PathBase を足す。</summary>
    /// <remarks>引数は要求の PathBase だけ。**宛先の決定へ要求の値は使わない。**</remarks>
    public string Resolve(PathString pathBase)
    {
        var target = Target
            ?? throw new InvalidOperationException($"{Setting} is not configured.");
        return target.StartsWith('/')
            ? pathBase.ToUriComponent() + target
            : target;
    }

    private static bool IsAcceptable(string value)
    {
        if (value.Length > MaximumLength)
        {
            return false;
        }

        // 空白（前後を含む）・制御文字・非 ASCII を落とす。Location へ入れたとき、
        // ヘッダの分割や、ブラウザごとの解釈の差が出ないようにする
        if (value.Any(character => character is < '!' or > '~'))
        {
            return false;
        }

        // バックスラッシュは、ブラウザが `/` として扱う（`/\host` がプロトコル相対になる）
        if (value.Contains('\\'))
        {
            return false;
        }

        if (value[0] == '/')
        {
            return value.Length == 1 || value[1] != '/';
        }

        var isHttp = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var isHttps = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
        {
            return false;
        }

        // 権限部（ホストの前後）に `@` があるものは、ユーザー情報の有無にかかわらず受け付けない
        var authorityStart = value.IndexOf("//", StringComparison.Ordinal) + 2;
        var authorityEnd = value.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        var authority = authorityEnd < 0
            ? value.AsSpan(authorityStart)
            : value.AsSpan(authorityStart, authorityEnd);
        if (authority.Contains('@'))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0;
    }
}
