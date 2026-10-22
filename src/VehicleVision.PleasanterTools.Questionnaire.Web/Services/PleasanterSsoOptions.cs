using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using CoreOptions = VehicleVision.PleasanterTools.Core.Web.PleasanterSsoOptions;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Core.Web の設定を、既存の設定キーと管理者の役割へ接続する。</summary>
public sealed class PleasanterSsoOptions : IPleasanterSsoVerificationOptions
{
    public const string AllowedDeptIdsKey = "QUESTIONNAIRE_PLEASANTERSSO_ALLOWEDDEPTIDS";
    public const string AllowedGroupIdsKey = "QUESTIONNAIRE_PLEASANTERSSO_ALLOWEDGROUPIDS";
    public const string EnabledKey = "QUESTIONNAIRE_PLEASANTERSSO_ENABLED";
    public const string InternalBaseUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_INTERNALBASEURL";
    public const string LoginUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_LOGINURL";
    public const string LogoutUrlKey = "QUESTIONNAIRE_PLEASANTERSSO_LOGOUTURL";
    public const string CookieNamesKey = "QUESTIONNAIRE_PLEASANTERSSO_COOKIENAMES";
    public const string UnknownUserKey = "QUESTIONNAIRE_PLEASANTERSSO_UNKNOWNUSER";
    public const string RegisterRoleKey = "QUESTIONNAIRE_PLEASANTERSSO_REGISTERROLE";
    public const string RevalidateMinutesKey = "QUESTIONNAIRE_PLEASANTERSSO_REVALIDATEMINUTES";
    public const string TimeoutSecondsKey = "QUESTIONNAIRE_PLEASANTERSSO_TIMEOUTSECONDS";
    public const string ButtonLabelKey = "QUESTIONNAIRE_PLEASANTERSSO_BUTTONLABEL";
    public const string OwnCookiePrefix = "q.";

    public const string DefaultCookieNames = CoreOptions.DefaultCookieNames;
    public const int DefaultRevalidateMinutes = CoreOptions.DefaultRevalidateMinutes;
    public const int MinimumRevalidateMinutes = CoreOptions.MinimumRevalidateMinutes;
    public const int MaximumRevalidateMinutes = CoreOptions.MaximumRevalidateMinutes;
    public const int DefaultTimeoutSeconds = CoreOptions.DefaultTimeoutSeconds;
    public const int MinimumTimeoutSeconds = CoreOptions.MinimumTimeoutSeconds;
    public const int MaximumTimeoutSeconds = CoreOptions.MaximumTimeoutSeconds;

    private static readonly PleasanterSsoSettingKeys Keys = new("QUESTIONNAIRE_");

    public ImmutableArray<int> AllowedDeptIds { get; init; } = [];
    public ImmutableArray<int> AllowedGroupIds { get; init; } = [];
    public bool HasMembershipRestriction => !AllowedDeptIds.IsDefaultOrEmpty || !AllowedGroupIds.IsDefaultOrEmpty;
    public bool Enabled { get; init; }
    public Uri? InternalBaseUrl { get; init; }
    public string LoginUrl { get; init; } = string.Empty;
    public string LogoutUrl { get; init; } = string.Empty;
    public ImmutableArray<string> CookieNamePrefixes { get; init; } = ParseCookieNames(DefaultCookieNames);
    public PleasanterSsoUnknownUserPolicy UnknownUser { get; init; }
    public AdminRole RegisterRole { get; init; } = AdminRole.Editor;
    public TimeSpan RevalidateInterval { get; init; } = TimeSpan.FromMinutes(DefaultRevalidateMinutes);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    public string ButtonLabel { get; init; } = string.Empty;

    public static PleasanterSsoOptions FromValues(Func<string, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);
        var options = AuthenticationOptionsAdapter.Read(() => CoreOptions.FromValues(
            key => key == RegisterRoleKey
                ? AuthenticationOptionsAdapter.ReadRegisterRole(valueOf(key), key, rejectNumeric: true)
                : valueOf(key),
            Keys, OwnCookiePrefix, AuthenticationOptionsAdapter.RegisterRoles,
            AuthenticationOptionsAdapter.DefaultRegisterRole));

        return new()
        {
            Enabled = options.Enabled,
            AllowedDeptIds = options.AllowedDeptIds,
            AllowedGroupIds = options.AllowedGroupIds,
            InternalBaseUrl = options.InternalBaseUrl,
            LoginUrl = options.LoginUrl,
            LogoutUrl = options.LogoutUrl,
            CookieNamePrefixes = options.CookieNamePrefixes,
            UnknownUser = options.UnknownUser,
            RegisterRole = Enum.Parse<AdminRole>(options.RegisterRole),
            RevalidateInterval = options.RevalidateInterval,
            Timeout = options.Timeout,
            ButtonLabel = options.ButtonLabel,
        };
    }

    public static ImmutableArray<string> ParseCookieNames(string raw) =>
        AuthenticationOptionsAdapter.Read(() => CoreOptions.ParseCookieNames(raw, Keys, OwnCookiePrefix));

    public bool ShouldForwardCookie(string name) =>
        !string.IsNullOrEmpty(name)
        && !name.StartsWith(OwnCookiePrefix, StringComparison.OrdinalIgnoreCase)
        && CookieNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
