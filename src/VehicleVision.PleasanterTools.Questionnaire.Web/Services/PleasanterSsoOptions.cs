using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>本アプリに居ない利用者が Pleasanter から来たときの扱い。</summary>
/// <remarks>SAML の <see cref="SamlUnknownUserPolicy"/> と同じ意味にそろえてある。</remarks>
public enum PleasanterSsoUnknownUserPolicy
{
    /// <summary>通さない。**既定。** 管理者は本アプリ側で先に作っておく。</summary>
    Reject = 0,

    /// <summary>その場で作って通す（JIT）。**役割は設定で決める。**</summary>
    Register = 1,
}

/// <summary>Pleasanter に本人を聞く方式。</summary>
public enum PleasanterSsoMethod
{
    /// <summary>
    /// 標準の API（<c>POST /api/users/get</c> を利用者 ID <c>Own</c> で絞る）で聞く。**既定。**
    /// Pleasanter 側に何も置かなくてよく、書き込みも無い。
    /// </summary>
    /// <remarks>
    /// 利用者の API 利用が禁止されていて 403 が返ったときは、本アプリの API キーがあれば
    /// <c>/api/sessions/set</c> で利用者 ID を得て、API キーで <c>/api/users/{id}/get</c> を引く。
    /// </remarks>
    StandardApi = 0,

    /// <summary>
    /// 登録済みの拡張 SQL（<c>POST /api/extended/sql</c>）で聞く。
    /// Pleasanter 側に SQL の定義ファイルを置いて再起動する必要がある。
    /// </summary>
    ExtendedSql = 1,
}

/// <summary>Pleasanter のログインで管理画面へ入れるようにする設定（Issue #464）。</summary>
/// <remarks>
/// <para>
/// **既定は無効。** 何も設定しなければ、今までどおりの入り方だけになる。
/// </para>
/// <para>
/// **仕組み。** ブラウザが持つ Pleasanter の cookie を**本アプリのサーバが** Pleasanter へ転送し、
/// ログイン中の本人を返させる。方式は <see cref="Method"/> で選ぶ（既定は標準の API）。
/// **API キーはブラウザへ渡さない**（規約 8）。cookie を転送する問い合わせには API キーを載せない。
/// </para>
/// <para>
/// ⚠️ **本アプリと Pleasanter を同じホスト名で動かすことが前提。**
/// cookie はホスト単位でしか届かない（<c>_documents/Pleasanter-SSO-運用手順書.md</c>）。
/// </para>
/// </remarks>
public sealed class PleasanterSsoOptions
{
    public const string EnabledKey = "QUESTIONNAIRE_PLEASANTERSSO_ENABLED";
    public const string InternalBaseUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_INTERNALBASEURL";
    public const string LoginUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_LOGINURL";
    public const string LogoutUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_LOGOUTURL";
    public const string MethodKey = "QUESTIONNAIRE_PLEASANTERSSO_METHOD";
    public const string SqlNameKey = "QUESTIONNAIRE_PLEASANTERSSO_SQLNAME";
    public const string CookieNamesKey = "QUESTIONNAIRE_PLEASANTERSSO_COOKIENAMES";
    public const string UnknownUserKey = "QUESTIONNAIRE_PLEASANTERSSO_UNKNOWNUSER";
    public const string RegisterRoleKey = "QUESTIONNAIRE_PLEASANTERSSO_REGISTERROLE";
    public const string RevalidateMinutesKey = "QUESTIONNAIRE_PLEASANTERSSO_REVALIDATEMINUTES";
    public const string TimeoutSecondsKey = "QUESTIONNAIRE_PLEASANTERSSO_TIMEOUTSECONDS";
    public const string ButtonLabelKey = "QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL";

    /// <summary>拡張 SQL の名前の既定値。手順書の定義例と同じ。</summary>
    public const string DefaultSqlName = "QuestionnaireWhoAmI";

    /// <summary>転送する cookie の名前（前方一致）の既定値。</summary>
    /// <remarks>
    /// **認証 cookie は分割されることがある**（<c>.AspNetCore.CookiesC1</c>、<c>C2</c> …）。
    /// 前方一致にしてあるので、分割されたものもまとめて送る。
    /// </remarks>
    public const string DefaultCookieNames = ".AspNetCore.Cookies,Pleasanter_SessionGuid";

    public const int DefaultRevalidateMinutes = 5;
    public const int MinimumRevalidateMinutes = 1;
    public const int MaximumRevalidateMinutes = 60;

    public const int DefaultTimeoutSeconds = 5;
    public const int MinimumTimeoutSeconds = 1;
    public const int MaximumTimeoutSeconds = 30;

    /// <summary>本アプリ自身の cookie の接頭辞。**設定に何を書いても転送しない。**</summary>
    /// <remarks>
    /// 本アプリの cookie はすべて <c>q.</c> で始まる（<c>q.admin</c>、<c>q.admin.pending</c>、
    /// <c>q.admin.saml</c>、<c>q.admin.rescue</c>、<c>q.asset</c> など）。
    /// **本アプリのセッションを Pleasanter へ渡さない。**
    /// </remarks>
    public const string OwnCookiePrefix = "q.";

    /// <summary>Pleasanter のログインを使うか。**既定は使わない。**</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 本アプリのサーバから Pleasanter へ問い合わせるときの URL（サーバ間）。
    /// **ブラウザには渡さない。**
    /// </summary>
    public Uri? InternalBaseUrl { get; init; }

    /// <summary>ブラウザで開く Pleasanter のログイン画面。</summary>
    /// <remarks>**同じホストの相対パス**（<c>/users/login</c>）でも絶対 URL でもよい。</remarks>
    public string LoginUrl { get; init; } = string.Empty;

    /// <summary>
    /// 本アプリからログアウトした後に開く Pleasanter のログアウト画面。
    /// **空なら本アプリからだけログアウトする。**
    /// </summary>
    public string LogoutUrl { get; init; } = string.Empty;

    /// <summary>本人を聞く方式。**既定は標準の API。**</summary>
    public PleasanterSsoMethod Method { get; init; } = PleasanterSsoMethod.StandardApi;

    /// <summary>本人を返す拡張 SQL の名前。**<see cref="PleasanterSsoMethod.ExtendedSql"/> のときだけ使う。**</summary>
    public string SqlName { get; init; } = DefaultSqlName;

    /// <summary>転送する cookie の名前（前方一致）。</summary>
    public ImmutableArray<string> CookieNamePrefixes { get; init; } = ParseCookieNames(DefaultCookieNames);

    /// <summary>本アプリに居ない利用者の扱い。**既定は通さない。**</summary>
    public PleasanterSsoUnknownUserPolicy UnknownUser { get; init; } = PleasanterSsoUnknownUserPolicy.Reject;

    /// <summary>JIT で作る利用者の役割。**既定は <see cref="AdminRole.Editor"/>。**</summary>
    /// <remarks>
    /// ⚠️ **ここを <see cref="AdminRole.Administrator"/> にすると、
    /// Pleasanter に居る全員が全権を持つ。**
    /// </remarks>
    public AdminRole RegisterRole { get; init; } = AdminRole.Editor;

    /// <summary>Pleasanter へ問い合わせ直す間隔。</summary>
    public TimeSpan RevalidateInterval { get; init; } = TimeSpan.FromMinutes(DefaultRevalidateMinutes);

    /// <summary>1 回の問い合わせを待つ長さ。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>ログイン画面の釦に出す文字。**空なら既定の文言。**</summary>
    public string ButtonLabel { get; init; } = string.Empty;

    /// <summary>拡張 SQL API の URL。</summary>
    public Uri ExtendedSqlUrl => new(
        InternalBaseUrl ?? throw new InvalidOperationException("Pleasanter のシングルサインオンは無効です。"),
        "api/extended/sql");

    /// <summary>鍵ごとの生値から設定を読む。**足りないものがあれば例外にする。**</summary>
    /// <remarks>
    /// **黙って無効へ落とさない。** 「有効にしたつもりが効いていない」を保存の時点で気付かせる。
    /// 無効のときも数値や列挙の書き間違いは拒否する（有効にした瞬間に壊れないように）。
    /// </remarks>
    /// <exception cref="InvalidOperationException">設定が読めない、または有効なのに足りない。</exception>
    public static PleasanterSsoOptions FromValues(Func<string, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        var enabled = ParseBoolean(valueOf(EnabledKey), EnabledKey);
        var internalBaseUrl = Trim(valueOf(InternalBaseUrlKey));
        var loginUrl = Trim(valueOf(LoginUrlKey));
        var logoutUrl = Trim(valueOf(LogoutUrlKey));
        var sqlName = Trim(valueOf(SqlNameKey));
        var cookieNames = Trim(valueOf(CookieNamesKey));

        var method = ParseEnum<PleasanterSsoMethod>(valueOf(MethodKey), MethodKey)
            ?? PleasanterSsoMethod.StandardApi;
        var unknownUser = ParseEnum<PleasanterSsoUnknownUserPolicy>(valueOf(UnknownUserKey), UnknownUserKey)
            ?? PleasanterSsoUnknownUserPolicy.Reject;
        var registerRole = ParseEnum<AdminRole>(valueOf(RegisterRoleKey), RegisterRoleKey)
            ?? AdminRole.Editor;
        var revalidateMinutes = ParseInteger(
            valueOf(RevalidateMinutesKey),
            RevalidateMinutesKey,
            DefaultRevalidateMinutes,
            MinimumRevalidateMinutes,
            MaximumRevalidateMinutes);
        var timeoutSeconds = ParseInteger(
            valueOf(TimeoutSecondsKey),
            TimeoutSecondsKey,
            DefaultTimeoutSeconds,
            MinimumTimeoutSeconds,
            MaximumTimeoutSeconds);

        var prefixes = cookieNames.Length == 0
            ? ParseCookieNames(DefaultCookieNames)
            : ParseCookieNames(cookieNames);

        Uri? internalUri = null;
        if (internalBaseUrl.Length > 0)
        {
            internalUri = ParseBaseUrl(internalBaseUrl);
        }

        if (loginUrl.Length > 0)
        {
            ValidateBrowserUrl(loginUrl, LoginUrlKey);
        }

        if (logoutUrl.Length > 0)
        {
            ValidateBrowserUrl(logoutUrl, LogoutUrlKey);
        }

        if (sqlName.Length > 0)
        {
            ValidateSqlName(sqlName);
        }

        if (enabled)
        {
            if (internalUri is null)
            {
                throw new InvalidOperationException(
                    $"{EnabledKey} を有効にしたときは {InternalBaseUrlKey} が要ります。");
            }

            if (loginUrl.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{EnabledKey} を有効にしたときは {LoginUrlKey} が要ります。");
            }
        }

        return new PleasanterSsoOptions
        {
            Enabled = enabled,
            InternalBaseUrl = internalUri,
            LoginUrl = loginUrl,
            LogoutUrl = logoutUrl,
            Method = method,
            SqlName = sqlName.Length == 0 ? DefaultSqlName : sqlName,
            CookieNamePrefixes = prefixes,
            UnknownUser = unknownUser,
            RegisterRole = registerRole,
            RevalidateInterval = TimeSpan.FromMinutes(revalidateMinutes),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            ButtonLabel = Trim(valueOf(ButtonLabelKey)),
        };
    }

    /// <summary>転送する cookie の名前（前方一致）を読む。</summary>
    /// <remarks>
    /// ⚠️ **本アプリの cookie（<c>q.</c> で始まるもの）に当たる指定は拒否する。**
    /// 空の指定（＝全部に当たる）も拒否する。
    /// </remarks>
    public static ImmutableArray<string> ParseCookieNames(string raw)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var part in (raw ?? string.Empty).Split(
            [',', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.All(IsCookieNameCharacter))
            {
                throw new InvalidOperationException(
                    $"{CookieNamesKey} に cookie の名前として使えない文字があります: {part}");
            }

            // **本アプリの cookie と前方一致で重なる指定は受け付けない。**
            // "q" や "q." を書かれると、本アプリのセッションを Pleasanter へ送ってしまう
            if (part.StartsWith(OwnCookiePrefix, StringComparison.OrdinalIgnoreCase)
                || OwnCookiePrefix.StartsWith(part, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{CookieNamesKey} に本アプリの cookie（{OwnCookiePrefix} で始まる名前）と重なる指定があります: {part}");
            }

            if (!builder.Contains(part, StringComparer.Ordinal))
            {
                builder.Add(part);
            }
        }

        if (builder.Count == 0)
        {
            throw new InvalidOperationException($"{CookieNamesKey} に転送する cookie の名前がありません。");
        }

        return builder.ToImmutable();
    }

    /// <summary>この cookie を Pleasanter へ転送してよいか。</summary>
    public bool ShouldForwardCookie(string name) =>
        !string.IsNullOrEmpty(name)
        && !name.StartsWith(OwnCookiePrefix, StringComparison.OrdinalIgnoreCase)
        && CookieNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>RFC 6265 の token に使える文字か。</summary>
    private static bool IsCookieNameCharacter(char character) =>
        character is > ' ' and < (char)0x7F
        && "()<>@,;:\\\"/[]?={}".IndexOf(character, StringComparison.Ordinal) < 0;

    private static Uri ParseBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"{InternalBaseUrlKey} は http(s) の絶対 URL で書いてください（問い合わせ文字列・資格情報は不可）: {value}");
        }

        // **末尾を / にそろえる。** 相対の結合で最後の区切りが落ちないように
        return uri.AbsolutePath.EndsWith('/')
            ? uri
            : new Uri(uri.GetLeftPart(UriPartial.Path) + "/");
    }

    /// <summary>ブラウザへ渡す URL を確かめる。</summary>
    /// <remarks>
    /// **同じホストの相対パス（<c>/</c> 始まり）か http(s) の絶対 URL だけ。**
    /// <c>javascript:</c> などを画面から開かせない。<c>//example.com</c> も外す。
    /// </remarks>
    private static void ValidateBrowserUrl(string value, string key)
    {
        if (value.StartsWith('/'))
        {
            if (value.StartsWith("//", StringComparison.Ordinal)
                || value.StartsWith("/\\", StringComparison.Ordinal)
                || value.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"{key} は / で始まる同じホストのパスか、http(s) の絶対 URL で書いてください: {value}");
            }

            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"{key} は / で始まる同じホストのパスか、http(s) の絶対 URL で書いてください: {value}");
        }
    }

    /// <summary>拡張 SQL の名前を確かめる。**JSON の本文に入れるだけだが、変な値は最初から断る。**</summary>
    private static void ValidateSqlName(string value)
    {
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{SqlNameKey} に使えない値が入っています。");
        }
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static bool ParseBoolean(string? value, string key)
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (!bool.TryParse(trimmed, out var parsed))
        {
            throw new InvalidOperationException($"{key} は true か false で指定してください: {trimmed}");
        }

        return parsed;
    }

    private static int ParseInteger(string? value, string key, int defaultValue, int minimum, int maximum)
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return defaultValue;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new InvalidOperationException(
                $"{key} は {minimum} 以上 {maximum} 以下の整数で指定してください: {trimmed}");
        }

        return parsed;
    }

    /// <summary>知らない値は例外にする。**書き間違いを黙って既定へ落とさない。**</summary>
    private static TEnum? ParseEnum<TEnum>(string? value, string key)
        where TEnum : struct, Enum
    {
        var trimmed = Trim(value);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Enum.TryParse<TEnum>(trimmed, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
            || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException(
                $"{key} に知らない値が入っています: {trimmed}"
                + $"（使えるのは {string.Join(" / ", Enum.GetNames<TEnum>())}）");
        }

        return parsed;
    }
}
