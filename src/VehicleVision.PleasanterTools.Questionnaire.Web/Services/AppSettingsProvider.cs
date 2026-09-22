using System.Collections.Frozen;
using System.Globalization;
using System.Net.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;

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
    string LabelJa,
    string LabelEn,
    string DescriptionJa,
    string DescriptionEn,
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
            throw new AppSettingValidationException(
                $"{LabelJa}は {maximumLength} 文字以内で入力してください。");
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
            throw new AppSettingValidationException($"{LabelJa}は真偽値で指定してください。");
        }

        return parsed ? "true" : "false";
    }

    private string NormalizeInteger(string value, bool enforceRange)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new AppSettingValidationException($"{LabelJa}は整数で指定してください。");
        }

        if (!enforceRange)
        {
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        if (Minimum is { } minimum && parsed < minimum)
        {
            throw new AppSettingValidationException($"{LabelJa}は {minimum} 以上で指定してください。");
        }

        if (Maximum is { } maximum && parsed > maximum)
        {
            throw new AppSettingValidationException($"{LabelJa}は {maximum} 以下で指定してください。");
        }

        return parsed.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>設定の入力または更新要求を受け付けられない。</summary>
public sealed class AppSettingValidationException(string message) : Exception(message);

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
            "管理者向けのお知らせ",
            "Administrator notice",
            "設定の保存と反映を確認するための項目です。入力した内容は下にプレビューされます。",
            "This field demonstrates saving and applying settings. The text is previewed below.",
            MaximumLength: 1000,
            ShowPreview: true),
        new(
            EmbedParentOptions.AllowedParentsKey,
            AppSettingValueType.String,
            string.Empty,
            "回答画面を埋め込める親サイト",
            "Parent sites allowed to embed forms",
            "ホストをカンマ区切りで指定します。www.example.net はそのホストだけ、*.example.net はその配下だけを許可します。",
            "Enter comma-separated hosts. www.example.net allows only that host; *.example.net allows only its subdomains.",
            MaximumLength: 2000,
            StringNormalizer: EmbedHostSettings.Normalize),
        new(
            EmbedOptions.AllowedHostsKey,
            AppSettingValueType.String,
            string.Empty,
            "回答画面へ埋め込める配信元",
            "Sources allowed in forms",
            "ホストをカンマ区切りで指定します。www.example.net はそのホストだけ、*.example.net はその配下だけを許可します。",
            "Enter comma-separated hosts. www.example.net allows only that host; *.example.net allows only its subdomains.",
            MaximumLength: 2000,
            StringNormalizer: EmbedHostSettings.Normalize),
        new(
            MailEnabledKey,
            AppSettingValueType.Boolean,
            "false",
            "メール送信を有効にする",
            "Enable email delivery",
            "有効にする前に、必須項目を入力して試験送信が届くことを確認してください。",
            "Before enabling, complete the required fields and confirm that a test message arrives."),
        new(
            MailTransportKey,
            AppSettingValueType.String,
            MailTransportKind.Smtp.ToString(),
            "送信方式",
            "Delivery transport",
            "SMTP、Amazon SES（IAM ロール）、Azure Communication Services（マネージド ID）から選びます。",
            "Choose SMTP, Amazon SES (IAM role), or Azure Communication Services (managed identity).",
            MaximumLength: 64,
            StringNormalizer: NormalizeMailTransport),
        new(
            MailSmtpHostKey,
            AppSettingValueType.String,
            string.Empty,
            "SMTP サーバー",
            "SMTP server",
            "SMTP を選んだときに使うホスト名です。",
            "Host name used when SMTP is selected.",
            MaximumLength: 255),
        new(
            MailSmtpPortKey,
            AppSettingValueType.Integer,
            "587",
            "SMTP ポート",
            "SMTP port",
            "既定は 587 です。Azure では外向きの 25 番ポートが制限されます。",
            "The default is 587. Azure restricts outbound port 25.",
            Minimum: 1,
            Maximum: 65535),
        new(
            MailSmtpSecurityKey,
            AppSettingValueType.String,
            SmtpSecurity.StartTls.ToString(),
            "SMTP の暗号化",
            "SMTP security",
            "587 番は StartTls、465 番は ImplicitTls が一般的です。None は検証用途だけにしてください。",
            "StartTls is typical for port 587 and ImplicitTls for 465. Use None only for testing.",
            MaximumLength: 32,
            StringNormalizer: NormalizeSmtpSecurity),
        new(
            MailSmtpUserKey,
            AppSettingValueType.String,
            string.Empty,
            "SMTP ユーザー名",
            "SMTP user name",
            "空の場合は SMTP 認証を行いません。",
            "Leave empty to skip SMTP authentication.",
            MaximumLength: 320),
        new(
            MailSmtpPasswordKey,
            AppSettingValueType.String,
            string.Empty,
            "SMTP パスワード",
            "SMTP password",
            "値は画面へ返しません。変更するときだけ入力してください。",
            "The value is never returned to the browser. Enter it only when changing it.",
            IsSecret: true,
            MaximumLength: 2000),
        new(
            MailFromAddressKey,
            AppSettingValueType.String,
            string.Empty,
            "差出人メールアドレス",
            "From address",
            "送信元としてメールに表示するアドレスです。",
            "Address shown as the sender.",
            MaximumLength: 320,
            StringNormalizer: value => NormalizeMailAddress(value, required: false)),
        new(
            MailFromNameKey,
            AppSettingValueType.String,
            string.Empty,
            "差出人名",
            "From name",
            "差出人の表示名です。空の場合はアドレスだけを表示します。",
            "Display name for the sender. Leave empty to show only the address.",
            MaximumLength: 200),
        new(
            MailReplyToAddressKey,
            AppSettingValueType.String,
            string.Empty,
            "返信先メールアドレス",
            "Reply-to address",
            "返信を受けるアドレスです。空の場合は差出人と同じです。",
            "Address that receives replies. Leave empty to use the sender address.",
            MaximumLength: 320,
            StringNormalizer: value => NormalizeMailAddress(value, required: false)),
        new(
            MailBaseUrlKey,
            AppSettingValueType.String,
            string.Empty,
            "公開 URL",
            "Public base URL",
            "招待メールなどのリンクに使います。メールを有効にするときは空にできません。",
            "Used for links in invitation messages. It is required when email is enabled.",
            MaximumLength: 2000,
            StringNormalizer: NormalizeHttpUrl),
        new(
            MailSesRegionKey,
            AppSettingValueType.String,
            string.Empty,
            "Amazon SES リージョン",
            "Amazon SES region",
            "AmazonSes を選んだときのリージョンです。資格情報は IAM ロールから取得します。",
            "Region used for AmazonSes. Credentials are obtained from the IAM role.",
            MaximumLength: 100),
        new(
            MailAcsEndpointKey,
            AppSettingValueType.String,
            string.Empty,
            "Azure Communication Services エンドポイント",
            "Azure Communication Services endpoint",
            "AzureCommunicationServices を選んだときの HTTPS エンドポイントです。資格情報はマネージド ID から取得します。",
            "HTTPS endpoint used for AzureCommunicationServices. Credentials are obtained from managed identity.",
            MaximumLength: 2000,
            StringNormalizer: NormalizeHttpsUrl),
        new(
            PleasanterBaseUrlKey,
            AppSettingValueType.String,
            string.Empty,
            "Pleasanter の URL",
            "Pleasanter URL",
            "Pleasanter のベース URL を指定します。変更すると、既存アンケートの回答も新しい接続先へ送信されます。",
            "Enter the Pleasanter base URL. Existing surveys will also send responses to the new destination.",
            MaximumLength: 2000,
            StringNormalizer: NormalizePleasanterBaseUrl),
        new(
            PleasanterApiVersionKey,
            AppSettingValueType.String,
            "1.1",
            "Pleasanter API バージョン",
            "Pleasanter API version",
            "Pleasanter API へ渡すバージョンです。",
            "The version sent to the Pleasanter API.",
            MaximumLength: 20,
            StringNormalizer: NormalizePleasanterApiVersion),
        new(
            PleasanterTimeZoneKey,
            AppSettingValueType.String,
            "Asia/Tokyo",
            "Pleasanter API キー利用者のタイムゾーン",
            "Pleasanter API key user time zone",
            "誤ると回答日時が静かにずれます。保存後、日時表示など全機能へ反映するにはアプリの再起動が必要です。",
            "An incorrect value silently shifts response times. Restart the application after saving to apply it to all date and time displays.",
            MaximumLength: 255,
            StringNormalizer: NormalizePleasanterTimeZone),
        new(
            PleasanterApiKeyKey,
            AppSettingValueType.String,
            string.Empty,
            "Pleasanter API キー",
            "Pleasanter API key",
            "値は画面へ返しません。空欄のまま保存すると現在の値を保持し、画面からは削除できません。",
            "The value is never returned to the browser. Saving an empty field keeps the current value; it cannot be deleted from this screen.",
            IsSecret: true,
            MaximumLength: 2000),
        new(
            BotMitigationOptionsProvider.MitigationEnabledKey,
            AppSettingValueType.Boolean,
            "true",
            "回答送信の bot 対策",
            "Bot protection for form submissions",
            "送信チケット、最短時間、ハニーポットを有効にします。無効化すると回答送信の bot 対策がなくなるため、検証環境以外では変更しないでください。",
            "Enables submission tickets, minimum elapsed time, and a honeypot. Do not disable outside test environments because form submissions will no longer have bot protection.",
            BooleanFalseAlias: "off"),
        new(
            BotMitigationOptionsProvider.SubmitMinimumSecondsKey,
            AppSettingValueType.Integer,
            "3",
            "回答送信までの最短時間（秒）",
            "Minimum time before submitting (seconds)",
            "チケット発行からこの時間より早い回答を拒否します。長くすると、素早く回答した利用者も拒否されます。",
            "Rejects responses submitted sooner than this after ticket issuance. Increasing it can also reject legitimate fast respondents.",
            Minimum: 1,
            Maximum: 60),
        new(
            BotMitigationOptionsProvider.SubmitTicketHoursKey,
            AppSettingValueType.Integer,
            "24",
            "送信チケットの有効期間（時間）",
            "Submission ticket lifetime (hours)",
            "期限を過ぎた回答は拒否します。短くすると、長い設問を回答中の利用者が送り直す必要があります。",
            "Rejects responses after the ticket expires. Reducing it can require respondents completing long forms to start over.",
            Minimum: 1,
            Maximum: 168),
        new(
            BotMitigationOptionsProvider.AltchaEnabledKey,
            AppSettingValueType.Boolean,
            "true",
            "回答者向け proof-of-work",
            "Proof of work for respondents",
            "回答者の端末で計算する自前の proof-of-work を有効にします。第三者へ回答者情報を送らないため、完全匿名の前提を保てます。",
            "Enables self-hosted proof of work computed on the respondent's device. It preserves anonymity because no respondent data is sent to a third party."),
        new(
            BotMitigationOptionsProvider.AltchaMinimumNumberKey,
            AppSettingValueType.Integer,
            "50000",
            "proof-of-work の探索下限",
            "Proof-of-work minimum search number",
            "回答者の端末で探す数の下限です。大きくすると bot の負担も増えますが、古い端末での待ち時間も伸びます。上限以下にしてください。",
            "Minimum number searched on the respondent's device. A larger value increases bot cost but also wait time on older devices. Keep it no greater than the maximum.",
            Minimum: 10_000,
            Maximum: 250_000),
        new(
            BotMitigationOptionsProvider.AltchaMaximumNumberKey,
            AppSettingValueType.Integer,
            "150000",
            "proof-of-work の探索上限",
            "Proof-of-work maximum search number",
            "回答者の端末で探す数の上限です。大きくすると bot の負担も増えますが、古い端末での待ち時間も伸びます。下限以上にしてください。",
            "Maximum number searched on the respondent's device. A larger value increases bot cost but also wait time on older devices. Keep it no less than the minimum.",
            Minimum: 50_000,
            Maximum: 500_000),
        new(
            BotMitigationOptionsProvider.LoginProofOfWorkKey,
            AppSettingValueType.Boolean,
            "false",
            "管理者ログインの proof-of-work",
            "Proof of work for administrator sign-in",
            "パスワードログインと招待受取に proof-of-work を課します。管理者がログインする前に追加の計算が必要になります。",
            "Requires proof of work for password sign-in and invitation acceptance. Administrators must complete additional computation before signing in."),
    ];

    private static readonly FrozenDictionary<string, AppSettingDefinition> Definitions =
        DefinitionList.ToFrozenDictionary(definition => definition.Key, StringComparer.Ordinal);

    private readonly SemaphoreSlim gate = new(1, 1);
    private CacheEntry? cache;

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
                values[definition.Key] = definition.Normalize(defaultValue);
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
                throw new AppSettingValidationException($"管理対象ではない設定です: {key}");
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

                throw new AppSettingValidationException(
                    $"{definition.LabelJa}は外部設定で固定されているため、画面から変更できません。");
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
            throw new AppSettingValidationException("メール送信を有効にするには差出人メールアドレスが必要です。");
        }

        if (values[MailBaseUrlKey].Length == 0)
        {
            throw new AppSettingValidationException("メール送信を有効にするには公開 URL が必要です。");
        }

        switch (Enum.Parse<MailTransportKind>(values[MailTransportKey]))
        {
            case MailTransportKind.Smtp when values[MailSmtpHostKey].Length == 0:
                throw new AppSettingValidationException("SMTP を使うには SMTP サーバーが必要です。");
            case MailTransportKind.AmazonSes when values[MailSesRegionKey].Length == 0:
                throw new AppSettingValidationException("Amazon SES を使うにはリージョンが必要です。");
            case MailTransportKind.AzureCommunicationServices when values[MailAcsEndpointKey].Length == 0:
                throw new AppSettingValidationException(
                    "Azure Communication Services を使うにはエンドポイントが必要です。");
        }
    }

    private static string NormalizeMailTransport(string value) =>
        Enum.TryParse<MailTransportKind>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : throw new AppSettingValidationException(
                    "送信方式は Smtp、AmazonSes、AzureCommunicationServices から選んでください。");

    private static string NormalizeSmtpSecurity(string value) =>
        Enum.TryParse<SmtpSecurity>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : throw new AppSettingValidationException(
                    "SMTP の暗号化は StartTls、ImplicitTls、None から選んでください。");

    private static string NormalizeMailAddress(string value, bool required)
    {
        if (value.Length == 0 && !required)
        {
            return string.Empty;
        }

        return MailAddress.TryCreate(value, out var address)
            && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
                ? address.Address
                : throw new AppSettingValidationException("メールアドレスの形式が正しくありません。");
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
            throw new AppSettingValidationException(
                requireHttps
                    ? "HTTPS の絶対 URL を入力してください。"
                    : "http:// または https:// から始まる絶対 URL を入力してください。");
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
            throw new AppSettingValidationException(
                "Pleasanter の URL は http または https の絶対 URL で指定してください。");
        }

        return value.TrimEnd('/');
    }

    /// <summary>外部設定の値をそろえる。**書式が壊れていても起動を止めない**（Issue #383）。</summary>
    /// <remarks>
    /// ⚠️ **起動を止めると、設定を直す手立てごと失う。**
    /// 打ち間違いは既定値へ落とし、**落としたことを起動時の記録へ出す。**
    /// 黙って既定へ戻るのが一番まずい。
    /// </remarks>
    private string NormalizeExternal(AppSettingDefinition definition, string value)
    {
        try
        {
            return definition.NormalizeExternal(value);
        }
        catch (AppSettingValidationException exception)
        {
            // **コンソールへ出す文字列は英語**（Azure の Kudu で日本語が化けるため）
            logger.LogWarning(
                "The value of {Setting} is invalid and the default value is used instead. {Reason}",
                definition.Key,
                exception.Message);
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
            throw new AppSettingValidationException(
                "Pleasanter API バージョンは 0 より大きい数値で指定してください。");
        }

        return version.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizePleasanterTimeZone(string value)
    {
        if (value.Length == 0)
        {
            throw new AppSettingValidationException(
                "Pleasanter API キー利用者のタイムゾーンを指定してください。");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
            return value;
        }
        catch (Exception exception)
            when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new AppSettingValidationException(
                "Pleasanter API キー利用者のタイムゾーンが見つかりません。");
        }
    }
}
