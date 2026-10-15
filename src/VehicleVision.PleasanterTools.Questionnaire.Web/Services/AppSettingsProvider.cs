using System.Collections.Frozen;
using System.Globalization;
using System.Net.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Scripting;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Worker;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理画面から変更できる設定の型。</summary>
public enum AppSettingValueType
{
    String,
    Boolean,
    Integer,
}

/// <summary>管理画面から変更できる設定の定義。</summary>
public sealed record AppSettingDefinition(
    string Key,
    AppSettingValueType Type,
    string DefaultValue,
    bool IsSecret = false,
    int? Minimum = null,
    int? Maximum = null,
    int? MaximumLength = null,
    bool ShowPreview = false,
    Func<string, string>? StringNormalizer = null,
    string? BooleanFalseAlias = null)
{
    /// <summary>入力を検証し、DB へ保存する表現へそろえる。</summary>
    public string Normalize(string? requestedValue) =>
        Normalize(requestedValue, enforceRange: true);

    /// <summary>外部設定の値をそろえる。**上限・下限は課さない**（Issue #383）。</summary>
    /// <remarks>
    /// **上限・下限は「管理者が画面から守りを弱められないようにする」ためのもの。**
    /// 環境変数や設定ファイルを書くのは運用者で、もともと何でも設定できる立場にある。
    /// ⚠️ **ここで課すと、検証環境のように意図して低くしている構成が起動しなくなる**
    /// （`compose.yaml` は proof-of-work を軽くするため既定より低い値を渡している）。
    /// </remarks>
    public string NormalizeExternal(string? value) =>
        Normalize(value, enforceRange: false);

    private string Normalize(string? requestedValue, bool enforceRange)
    {
        var value = requestedValue?.Trim() ?? string.Empty;
        return Type switch
        {
            AppSettingValueType.String => NormalizeString(value),
            AppSettingValueType.Boolean => NormalizeBoolean(value),
            AppSettingValueType.Integer => NormalizeInteger(value, enforceRange),
            _ => throw new InvalidOperationException($"未対応の設定型です: {Type}"),
        };
    }

    private string NormalizeString(string value)
    {
        if (MaximumLength is { } maximumLength && value.Length > maximumLength)
        {
            throw AppSettingValidationException.Of(
                ServerMessageKeys.SettingValueTooLong, Key, maximumLength);
        }

        return StringNormalizer?.Invoke(value) ?? value;
    }

    private string NormalizeBoolean(string value)
    {
        if (BooleanFalseAlias is not null
            && string.Equals(value, BooleanFalseAlias, StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.SettingNotBoolean, Key);
        }

        return parsed ? "true" : "false";
    }

    private string NormalizeInteger(string value, bool enforceRange)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.SettingNotInteger, Key);
        }

        if (!enforceRange)
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        if (Minimum is { } minimum && parsed < minimum)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.SettingBelowMinimum, Key, minimum);
        }

        if (Maximum is { } maximum && parsed > maximum)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.SettingAboveMaximum, Key, maximum);
        }

        return parsed.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>設定の入力または更新要求を受け付けられない。</summary>
public sealed class AppSettingValidationException : Exception
{
    public AppSettingValidationException(string message)
        : base(message)
    {
    }

    private AppSettingValidationException(string message, string messageKey, object[] arguments)
        : base(message)
    {
        MessageKey = messageKey;
        Arguments = arguments;
    }

    /// <summary>画面へ返す文言の鍵（Issue #605）。**日本語直書きの例外では <c>null</c>。**</summary>
    public string? MessageKey { get; }

    /// <summary>文言へ差し込む値（<c>{0}</c> から順）。</summary>
    public IReadOnlyList<object> Arguments { get; } = [];

    /// <summary>鍵と引数で作る。<see cref="Exception.Message"/> は日本語（ログ・フォールバック用）。</summary>
    public static AppSettingValidationException Of(string messageKey, params object[] arguments) =>
        new(
            Localization.ServerMessages.Get(messageKey, "ja", arguments),
            messageKey,
            arguments);

    /// <summary>画面の言語に合わせた文言。**鍵を持たない例外は <see cref="Exception.Message"/> のまま。**</summary>
    public string MessageFor(string? language) =>
        MessageKey is null
            ? Message
            : Localization.ServerMessages.Get(MessageKey, language, [.. Arguments]);
}

/// <summary>外部設定、DB、既定値から解決したアプリケーション設定。</summary>
public sealed record AppSettingsSnapshot(
    IReadOnlyList<AppSettingDefinition> Definitions,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> FixedKeys)
{
    public string this[string key] => Values[key];
}

/// <summary>アプリケーション設定を解決して保存する。</summary>
public interface IAppSettingsProvider
{
    Task<AppSettingsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    Task<AppSettingsSnapshot> SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        Guid updatedByAdminUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>外部設定 → DB → 既定値の順で設定を解決し、短時間だけ保持する。</summary>
public sealed class AppSettingsProvider(
    IConfiguration configuration,
    IAppSettingStore store,
    SecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<AppSettingsProvider> logger) : IAppSettingsProvider
{
    /// <summary>管理画面の設定区画が機能することを示す、管理者向けのお知らせ。</summary>
    public const string AdminNoticeKey = "QUESTIONNAIRE_ADMIN_NOTICE";
    public const string PleasanterBaseUrlKey = "QUESTIONNAIRE_PLEASANTER_BASEURL";
    public const string PleasanterApiVersionKey = "QUESTIONNAIRE_PLEASANTER_APIVERSION";
    public const string PleasanterTimeZoneKey = "QUESTIONNAIRE_PLEASANTER_TIMEZONE";
    public const string PleasanterApiKeyKey = "QUESTIONNAIRE_PLEASANTER_APIKEY";

    public const string MailEnabledKey = "QUESTIONNAIRE_MAIL_ENABLED";
    public const string MailTransportKey = "QUESTIONNAIRE_MAIL_TRANSPORT";
    public const string MailSmtpHostKey = "QUESTIONNAIRE_MAIL_SMTP_HOST";
    public const string MailSmtpPortKey = "QUESTIONNAIRE_MAIL_SMTP_PORT";
    public const string MailSmtpSecurityKey = "QUESTIONNAIRE_MAIL_SMTP_SECURITY";
    public const string MailSmtpUserKey = "QUESTIONNAIRE_MAIL_SMTP_USER";
    public const string MailSmtpPasswordKey = "QUESTIONNAIRE_MAIL_SMTP_PASSWORD";
    public const string MailFromAddressKey = "QUESTIONNAIRE_MAIL_FROM_ADDRESS";
    public const string MailFromNameKey = "QUESTIONNAIRE_MAIL_FROM_NAME";
    public const string MailReplyToAddressKey = "QUESTIONNAIRE_MAIL_REPLYTO_ADDRESS";
    public const string MailBaseUrlKey = "QUESTIONNAIRE_MAIL_BASEURL";
    public const string MailSesRegionKey = "QUESTIONNAIRE_MAIL_SES_REGION";
    public const string MailAcsEndpointKey = "QUESTIONNAIRE_MAIL_ACS_ENDPOINT";

    /// <summary>
    /// Redis は任意なので、短い TTL で全インスタンスが DB の更新へ必ず追いつく構成にする。
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private static readonly AppSettingDefinition[] DefinitionList =
    [
        new(
            AdminNoticeKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 1000,
            ShowPreview: true),
        new(
            EmbedParentOptions.AllowedParentsKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 2000,
            StringNormalizer: EmbedHostSettings.Normalize),
        new(
            EmbedOptions.AllowedHostsKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 2000,
            StringNormalizer: EmbedHostSettings.Normalize),
        new(
            ColorSchemeOptions.ColorSchemeKey,
            AppSettingValueType.String,
            ThemeColorScheme.System.ToString(),
            MaximumLength: 16,
            StringNormalizer: ColorSchemeOptions.Normalize),
        new(
            MailEnabledKey,
            AppSettingValueType.Boolean,
            "false"),
        new(
            MailTransportKey,
            AppSettingValueType.String,
            MailTransportKind.Smtp.ToString(),
            MaximumLength: 64,
            StringNormalizer: NormalizeMailTransport),
        new(
            MailSmtpHostKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 255),
        new(
            MailSmtpPortKey,
            AppSettingValueType.Integer,
            "587",
            Minimum: 1,
            Maximum: 65535),
        new(
            MailSmtpSecurityKey,
            AppSettingValueType.String,
            SmtpSecurity.StartTls.ToString(),
            MaximumLength: 32,
            StringNormalizer: NormalizeSmtpSecurity),
        new(
            MailSmtpUserKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 320),
        new(
            MailSmtpPasswordKey,
            AppSettingValueType.String,
            string.Empty,
            IsSecret: true,
            MaximumLength: 2000),
        new(
            MailFromAddressKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 320,
            StringNormalizer: value => NormalizeMailAddress(value, required: false)),
        new(
            MailFromNameKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 200),
        new(
            MailReplyToAddressKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 320,
            StringNormalizer: value => NormalizeMailAddress(value, required: false)),
        new(
            MailBaseUrlKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 2000,
            StringNormalizer: NormalizeHttpUrl),
        new(
            MailSesRegionKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 100),
        new(
            MailAcsEndpointKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 2000,
            StringNormalizer: NormalizeHttpsUrl),
        new(
            PleasanterBaseUrlKey,
            AppSettingValueType.String,
            string.Empty,
            MaximumLength: 2000,
            StringNormalizer: NormalizePleasanterBaseUrl),
        new(
            PleasanterApiVersionKey,
            AppSettingValueType.String,
            "1.1",
            MaximumLength: 20,
            StringNormalizer: NormalizePleasanterApiVersion),
        new(
            PleasanterTimeZoneKey,
            AppSettingValueType.String,
            "Asia/Tokyo",
            MaximumLength: 255,
            StringNormalizer: NormalizePleasanterTimeZone),
        new(
            PleasanterApiKeyKey,
            AppSettingValueType.String,
            string.Empty,
            IsSecret: true,
            MaximumLength: 2000),
        new(
            BotMitigationOptionsProvider.MitigationEnabledKey,
            AppSettingValueType.Boolean,
            "true",
            BooleanFalseAlias: "off"),
        new(
            BotMitigationOptionsProvider.SubmitMinimumSecondsKey,
            AppSettingValueType.Integer,
            "3",
            Minimum: 1,
            Maximum: 60),
        new(
            BotMitigationOptionsProvider.SubmitTicketHoursKey,
            AppSettingValueType.Integer,
            "24",
            Minimum: 1,
            Maximum: 168),
        new(
            BotMitigationOptionsProvider.AltchaEnabledKey,
            AppSettingValueType.Boolean,
            "true"),
        new(
            BotMitigationOptionsProvider.AltchaMinimumNumberKey,
            AppSettingValueType.Integer,
            "50000",
            Minimum: 10_000,
            Maximum: 250_000),
        new(
            BotMitigationOptionsProvider.AltchaMaximumNumberKey,
            AppSettingValueType.Integer,
            "150000",
            Minimum: 50_000,
            Maximum: 500_000),
        new(
            BotMitigationOptionsProvider.LoginProofOfWorkKey,
            AppSettingValueType.Boolean,
            "false"),
        new(
            AuditLogRetentionOptions.RetentionDaysKey,
            AppSettingValueType.Integer,
            "365",
            Minimum: 1,
            Maximum: 36500),
        new(
            AuditLogRetentionOptions.DeadLetterRetentionDaysKey,
            AppSettingValueType.Integer,
            "0",
            Minimum: 1,
            Maximum: 36500),
        new(
            AuditLogRetentionOptions.NotificationRetentionDaysKey,
            AppSettingValueType.Integer,
            "90",
            Minimum: 1,
            Maximum: 36500),
        new(
            AuditLogRetentionOptions.AttachmentRejectionRetentionDaysKey,
            AppSettingValueType.Integer,
            "90",
            Minimum: 1,
            Maximum: 36500),
        new(
            "QUESTIONNAIRE_REQUESTS_PER_MIN",
            AppSettingValueType.Integer,
            "60",
            Minimum: 1,
            Maximum: 1000000),
        new(
            "QUESTIONNAIRE_FORM_REQUESTS_PER_MIN",
            AppSettingValueType.Integer,
            "600",
            Minimum: 1,
            Maximum: 1000000),
        new(
            "QUESTIONNAIRE_SUBMITS_PER_MIN",
            AppSettingValueType.Integer,
            "20",
            Minimum: 1,
            Maximum: 1000000),
        new(
            "QUESTIONNAIRE_ASSET_REQUESTS_PER_MIN",
            AppSettingValueType.Integer,
            "600",
            Minimum: 1,
            Maximum: 1000000),
        new(
            "QUESTIONNAIRE_LOGIN_ATTEMPTS_PER_5MIN",
            AppSettingValueType.Integer,
            "10",
            Minimum: 1,
            Maximum: 100000),
        new(
            MailSenderOptions.MaxSendsPerMinuteKey,
            AppSettingValueType.Integer,
            "60",
            Minimum: 1,
            Maximum: 1000000),
        new(
            ResponseSenderOptions.MaxSendsPerMinuteKey,
            AppSettingValueType.Integer,
            "600",
            Minimum: 1,
            Maximum: 1000000),
        new(
            BacklogGuardOptions.PerSurveyLimitKey,
            AppSettingValueType.Integer,
            "10000",
            Minimum: 1,
            Maximum: 10000000),
        new(
            BacklogGuardOptions.TotalLimitKey,
            AppSettingValueType.Integer,
            "50000",
            Minimum: 1,
            Maximum: 100000000),
        new(
            "QUESTIONNAIRE_ATTACHMENT_MAXFILESIZEBYTES",
            AppSettingValueType.Integer,
            "5242880",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            "QUESTIONNAIRE_ATTACHMENT_MAXFILECOUNT",
            AppSettingValueType.Integer,
            "5",
            Minimum: 1,
            Maximum: 1000),
        new(
            "QUESTIONNAIRE_ATTACHMENT_MAXTOTALBYTES",
            AppSettingValueType.Integer,
            "20971520",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            "QUESTIONNAIRE_ATTACHMENT_ALLOWEDEXTENSIONS",
            AppSettingValueType.String,
            string.Join(", ", AttachmentOptions.DefaultAllowedExtensions),
            MaximumLength: 1000,
            StringNormalizer: AttachmentOptions.NormalizeAllowedExtensions),
        new(
            "QUESTIONNAIRE_ASSET_MAXFILESIZEBYTES",
            AppSettingValueType.Integer,
            "10485760",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            "QUESTIONNAIRE_ASSET_MAXFILECOUNT",
            AppSettingValueType.Integer,
            "20",
            Minimum: 1,
            Maximum: 1000),
        new(
            "QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS",
            AppSettingValueType.String,
            string.Join(", ", AssetOptions.DefaultAllowedExtensions),
            MaximumLength: 1000,
            StringNormalizer: AssetOptions.NormalizeAllowedExtensions),
        new(
            ScriptConverterOptions.TimeLimitKey,
            AppSettingValueType.Integer,
            "200",
            Minimum: 1,
            Maximum: 60000),
        new(
            ScriptConverterOptions.MemoryLimitKey,
            AppSettingValueType.Integer,
            "4194304",
            Minimum: 1,
            Maximum: int.MaxValue),
        new(
            ScriptConverterOptions.RecursionLimitKey,
            AppSettingValueType.Integer,
            "64",
            Minimum: 1,
            Maximum: 1024),
        new(
            ParameterFiles.TimeZoneDefaultKey,
            AppSettingValueType.String,
            "Asia/Tokyo",
            MaximumLength: 100,
            StringNormalizer: NormalizeTimeZone),
        new(
            ResponseNotificationMailerOptions.DigestIntervalMinutesKey,
            AppSettingValueType.Integer,
            "1440",
            Minimum: 60,
            Maximum: 525600),
        new(
            "QUESTIONNAIRE_PLEASANTER_TIMEOUTSECONDS",
            AppSettingValueType.Integer,
            "30",
            Minimum: 1,
            Maximum: 3600),
        new(
            "QUESTIONNAIRE_VIRUSSCAN_TIMEOUTSECONDS",
            AppSettingValueType.Integer,
            "30",
            Minimum: 1,
            Maximum: 3600),
        new(
            "QUESTIONNAIRE_VIRUSSCAN_DEFENDER_RESULTTIMEOUTSECONDS",
            AppSettingValueType.Integer,
            "300",
            Minimum: 1,
            Maximum: 86400),
    ];

    private static readonly FrozenDictionary<string, AppSettingDefinition> Definitions =
        DefinitionList.ToFrozenDictionary(definition => definition.Key, StringComparer.Ordinal);

    private readonly SemaphoreSlim gate = new(1, 1);
    private CacheEntry? cache;

    /// <summary>DB を読めるようになる前に使う、外部設定と既定値だけのスナップショット。</summary>
    public static AppSettingsSnapshot InitialSnapshot(IConfiguration configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in DefinitionList)
        {
            if (configuration[definition.Key] is { } external)
            {
                // ⚠️ **外部設定をここで直に正規化しないこと**（Issue #395）。
                // 上限・下限を課してしまい、検証環境の設定で起動できなくなる
                values[definition.Key] = NormalizeExternal(definition, external, logger: null);
                fixedKeys.Add(definition.Key);
            }
            else
            {
                values[definition.Key] = definition.DefaultValue;
            }
        }

        if (configuration[PleasanterTimeZoneKey] is null)
        {
            values[PleasanterTimeZoneKey] = values[ParameterFiles.TimeZoneDefaultKey];
        }

        return new AppSettingsSnapshot(
            DefinitionList,
            values.ToFrozenDictionary(StringComparer.Ordinal),
            fixedKeys.ToFrozenSet(StringComparer.Ordinal));
    }

    public async Task<AppSettingsSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (Volatile.Read(ref cache) is { } current && now < current.ExpiresAt)
        {
            return current.Snapshot;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = timeProvider.GetUtcNow();
            if (Volatile.Read(ref cache) is { } cached && now < cached.ExpiresAt)
            {
                return cached.Snapshot;
            }

            return Cache(await LoadAsync(cancellationToken).ConfigureAwait(false), now);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsSnapshot> SaveAsync(
        IReadOnlyDictionary<string, string?> values,
        Guid updatedByAdminUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var normalized = NormalizeRequestedValues(values, current);
            var merged = Merge(current.Values, normalized);
            ValidateMailSettings(merged);

            foreach (var (key, value) in normalized)
            {
                var definition = Definitions[key];
                await store.SaveAsync(
                    key,
                    definition.IsSecret ? secretProtector.Protect(value) : value,
                    definition.IsSecret,
                    updatedByAdminUserId,
                    cancellationToken).ConfigureAwait(false);
            }

            return Cache(
                current with { Values = merged.ToFrozenDictionary(StringComparer.Ordinal) },
                timeProvider.GetUtcNow());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AppSettingsSnapshot> PreviewAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var current = await GetAsync(cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeRequestedValues(values, current);
        var merged = Merge(current.Values, normalized);
        ValidateMailSettings(merged);
        return current with { Values = merged.ToFrozenDictionary(StringComparer.Ordinal) };
    }

    private AppSettingsSnapshot Cache(AppSettingsSnapshot loaded, DateTimeOffset now)
    {
        Volatile.Write(ref cache, new CacheEntry(loaded, now + CacheLifetime));
        return loaded;
    }

    private async Task<AppSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var records = (await store.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(record => record.SettingKey, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in DefinitionList)
        {
            if (configuration[definition.Key] is { } external)
            {
                values[definition.Key] = NormalizeExternal(definition, external);
                fixedKeys.Add(definition.Key);
                continue;
            }

            if (!records.TryGetValue(definition.Key, out var record))
            {
                var defaultValue = definition.Key == PleasanterTimeZoneKey
                    ? configuration[ParameterFiles.TimeZoneDefaultKey] ?? definition.DefaultValue
                    : definition.DefaultValue;
                values[definition.Key] =
                    definition.Key == AuditLogRetentionOptions.DeadLetterRetentionDaysKey
                        ? defaultValue
                        : definition.Normalize(defaultValue);
                continue;
            }

            if (record.IsSecret != definition.IsSecret)
            {
                throw new InvalidOperationException(
                    $"設定の秘密区分が定義と一致しません: {definition.Key}");
            }

            var stored = definition.IsSecret
                ? secretProtector.Unprotect(record.Value)
                    ?? throw new InvalidOperationException($"設定を復号できません: {definition.Key}")
                : record.Value;
            values[definition.Key] = definition.Normalize(stored);
        }

        if (configuration[PleasanterTimeZoneKey] is null
            && !records.ContainsKey(PleasanterTimeZoneKey))
        {
            values[PleasanterTimeZoneKey] = values[ParameterFiles.TimeZoneDefaultKey];
        }

        return new AppSettingsSnapshot(
            DefinitionList,
            values.ToFrozenDictionary(StringComparer.Ordinal),
            fixedKeys.ToFrozenSet(StringComparer.Ordinal));
    }

    private IReadOnlyDictionary<string, string> NormalizeRequestedValues(
        IReadOnlyDictionary<string, string?> values,
        AppSettingsSnapshot current)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, requestedValue) in values)
        {
            if (!Definitions.TryGetValue(key, out var definition))
            {
                throw AppSettingValidationException.Of(ServerMessageKeys.SettingKeyNotManaged, key);
            }

            if (definition.IsSecret && string.IsNullOrWhiteSpace(requestedValue))
            {
                continue;
            }

            var value = definition.Normalize(requestedValue);
            if (current.FixedKeys.Contains(key))
            {
                if (string.Equals(value, current[key], StringComparison.Ordinal))
                {
                    continue;
                }

                throw AppSettingValidationException.Of(
                    ServerMessageKeys.SettingFixedExternally, key);
            }

            normalized[key] = value;
        }

        return normalized;
    }

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> replacement)
    {
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in replacement)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static void ValidateMailSettings(IReadOnlyDictionary<string, string> values)
    {
        if (!bool.Parse(values[MailEnabledKey]))
        {
            return;
        }

        if (values[MailFromAddressKey].Length == 0)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.MailFromAddressRequired);
        }

        if (values[MailBaseUrlKey].Length == 0)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.MailBaseUrlRequired);
        }

        switch (Enum.Parse<MailTransportKind>(values[MailTransportKey]))
        {
            case MailTransportKind.Smtp when values[MailSmtpHostKey].Length == 0:
                throw AppSettingValidationException.Of(ServerMessageKeys.SmtpHostRequired);
            case MailTransportKind.AmazonSes when values[MailSesRegionKey].Length == 0:
                throw AppSettingValidationException.Of(ServerMessageKeys.SesRegionRequired);
            case MailTransportKind.AzureCommunicationServices when values[MailAcsEndpointKey].Length == 0:
                throw AppSettingValidationException.Of(ServerMessageKeys.AcsEndpointRequired);
        }
    }

    private static string NormalizeMailTransport(string value) =>
        Enum.TryParse<MailTransportKind>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : throw AppSettingValidationException.Of(ServerMessageKeys.MailTransportInvalid);

    private static string NormalizeSmtpSecurity(string value) =>
        Enum.TryParse<SmtpSecurity>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : throw AppSettingValidationException.Of(ServerMessageKeys.SmtpSecurityInvalid);

    private static string NormalizeMailAddress(string value, bool required)
    {
        if (value.Length == 0 && !required)
        {
            return string.Empty;
        }

        return MailAddress.TryCreate(value, out var address)
            && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
                ? address.Address
                : throw AppSettingValidationException.Of(ServerMessageKeys.MailAddressInvalid);
    }

    private static string NormalizeHttpUrl(string value) =>
        NormalizeUrl(value, requireHttps: false);

    private static string NormalizeHttpsUrl(string value) =>
        NormalizeUrl(value, requireHttps: true);

    private static string NormalizeUrl(string value, bool requireHttps)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (requireHttps ? uri.Scheme != Uri.UriSchemeHttps
                : uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw AppSettingValidationException.Of(
                requireHttps ? ServerMessageKeys.HttpsUrlRequired : ServerMessageKeys.HttpUrlRequired);
        }

        return value.TrimEnd('/');
    }

    private sealed record CacheEntry(AppSettingsSnapshot Snapshot, DateTimeOffset ExpiresAt);

    private static string NormalizePleasanterBaseUrl(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.PleasanterUrlInvalid);
        }

        return value.TrimEnd('/');
    }

    /// <summary>外部設定の値をそろえる。**書式が壊れていても起動を止めない**（Issue #383）。</summary>
    /// <remarks>
    /// ⚠️ **起動を止めると、設定を直す手立てごと失う。**
    /// 打ち間違いは既定値へ落とし、**落としたことを起動時の記録へ出す。**
    /// 黙って既定へ戻るのが一番まずい。
    /// </remarks>
    private string NormalizeExternal(AppSettingDefinition definition, string value) =>
        NormalizeExternal(definition, value, logger);

    /// <summary>
    /// 外部設定の値をそろえる。**外部設定を読む入口はここ 1 つにする**（Issue #395）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **同じ判断を 2 か所へ書かないこと。** `InitialSnapshot` が別に
    /// <see cref="AppSettingDefinition.Normalize(string?)"/> を呼んでいたため、
    /// #390 で直したはずの「外部設定に上限・下限を課さない」が起動時だけ効かず、
    /// **検証環境の設定で本アプリが起動しなくなった**（Issue #395）。
    /// </remarks>
    private static string NormalizeExternal(
        AppSettingDefinition definition,
        string value,
        ILogger? logger)
    {
        try
        {
            return definition.NormalizeExternal(value);
        }
        catch (AppSettingValidationException exception)
        {
            // **コンソールへ出す文字列は英語**（Azure の Kudu で日本語が化けるため）
            logger?.LogWarning(
                "The value of {Setting} is invalid and the default value is used instead. {Reason}",
                definition.Key,
                exception.MessageFor("en"));
            return definition.Normalize(definition.DefaultValue);
        }
    }

    private static string NormalizePleasanterApiVersion(string value)
    {
        if (!decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var version)
            || version <= 0)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.PleasanterApiVersionInvalid);
        }

        return version.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizePleasanterTimeZone(string value)
    {
        if (value.Length == 0)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.PleasanterTimeZoneRequired);
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
            return value;
        }
        catch (Exception exception)
            when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.PleasanterTimeZoneNotFound);
        }
    }

    private static string NormalizeTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.DefaultTimeZoneRequired);
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value).Id;
        }
        catch (TimeZoneNotFoundException)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.TimeZoneIdUnresolved, value);
        }
        catch (InvalidTimeZoneException)
        {
            throw AppSettingValidationException.Of(ServerMessageKeys.TimeZoneIdInvalid, value);
        }
    }
}
