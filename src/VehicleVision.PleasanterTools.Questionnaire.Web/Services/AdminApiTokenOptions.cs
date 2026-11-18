namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理 API の個人トークン（API キー）の設定（Issue #1025。親 #1012）。</summary>
/// <remarks>
/// <para>
/// <b>機能は既定で無効。</b>無効のとき、発行はできず、発行済みのトークンも使えない
/// （一覧と失効は使える。漏れたトークンを止められなくならないように）。
/// </para>
/// <para>
/// <b>知らない値は起動時に落とす。</b>黙って既定へ落ちると、有効にしたつもりで無効のまま動く
/// （<c>_documents/管理APIのトークン認証-方針案.md</c> 3.3 章）。
/// </para>
/// <para>
/// <b>期限は必須。無期限は作れない。</b>既定の日数と上限は設定で変えられるが、上限は
/// <see cref="AbsoluteMaxDays"/>（365 日）を超えられない。
/// </para>
/// </remarks>
public sealed record AdminApiTokenOptions
{
    /// <summary>機能を有効にするか決める設定。<c>disabled</c>（既定）または <c>enabled</c>。</summary>
    public const string ModeSetting = "QUESTIONNAIRE_ADMIN_API_TOKENS";

    /// <summary>期限を指定しなかったときの日数の設定。</summary>
    public const string DefaultDaysSetting = "QUESTIONNAIRE_ADMIN_API_TOKEN_DEFAULT_DAYS";

    /// <summary>期限として選べる最長の日数の設定。</summary>
    public const string MaxDaysSetting = "QUESTIONNAIRE_ADMIN_API_TOKEN_MAX_DAYS";

    /// <summary>SSO の管理者が、直前の再ログインとみなす分数の設定。</summary>
    public const string ReloginMinutesSetting = "QUESTIONNAIRE_ADMIN_API_TOKEN_RELOGIN_MINUTES";

    /// <summary>期限の上限として許す最大の日数。<b>設定でもこれを超えられない。</b></summary>
    public const int AbsoluteMaxDays = 365;

    /// <summary>既定の期限（日）。</summary>
    public const int DefaultDefaultDays = 90;

    /// <summary>SSO の管理者の「直前の再ログイン」の既定（分）。</summary>
    public const int DefaultReloginMinutes = 5;

    /// <summary>SSO の管理者の「直前の再ログイン」として設定できる最大（分）。</summary>
    public const int AbsoluteMaxReloginMinutes = 15;

    /// <summary>持ち主 1 人あたりの、いま使えるトークンの上限。</summary>
    public const int MaxActivePerOwner = 20;

    /// <summary>トークン 1 本に付けられる許可 CIDR の上限。</summary>
    public const int MaxIpAllowListEntries = 10;

    /// <summary>機能を有効にするか。</summary>
    public bool Enabled { get; init; }

    /// <summary>期限を指定しなかったときの日数。</summary>
    public int DefaultDays { get; init; } = DefaultDefaultDays;

    /// <summary>期限として選べる最長の日数。</summary>
    public int MaxDays { get; init; } = AbsoluteMaxDays;

    /// <summary>SSO の管理者が、直前の再ログインとみなす長さ。</summary>
    public TimeSpan ReloginWindow { get; init; } = TimeSpan.FromMinutes(DefaultReloginMinutes);

    /// <summary>設定を読む。範囲外・解釈できない値は、設定名つきで起動を止める。</summary>
    public static AdminApiTokenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ParseMode(configuration[ModeSetting]);
        var maxDays = ParseInt(configuration[MaxDaysSetting], MaxDaysSetting, AbsoluteMaxDays, 1, AbsoluteMaxDays);
        var defaultDays = ParseInt(
            configuration[DefaultDaysSetting], DefaultDaysSetting, Math.Min(DefaultDefaultDays, maxDays), 1, maxDays);
        var reloginMinutes = ParseInt(
            configuration[ReloginMinutesSetting],
            ReloginMinutesSetting,
            DefaultReloginMinutes,
            1,
            AbsoluteMaxReloginMinutes);

        return new AdminApiTokenOptions
        {
            Enabled = enabled,
            DefaultDays = defaultDays,
            MaxDays = maxDays,
            ReloginWindow = TimeSpan.FromMinutes(reloginMinutes),
        };
    }

    private static bool ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "disabled" => false,
            "enabled" => true,
            _ => throw new InvalidOperationException(
                $"{ModeSetting} must be disabled or enabled (current value: {raw.Trim()})."),
        };
    }

    private static int ParseInt(string? raw, string setting, int fallback, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max
                ? value
                : throw new InvalidOperationException(
                    $"{setting} must be an integer from {min} to {max} (current value: {raw.Trim()}).");
    }
}
