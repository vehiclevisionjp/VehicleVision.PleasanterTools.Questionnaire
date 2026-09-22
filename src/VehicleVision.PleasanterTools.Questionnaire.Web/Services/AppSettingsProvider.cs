using System.Collections.Frozen;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Data;

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
    Func<string, string>? StringNormalizer = null)
{
    /// <summary>入力を検証し、DB へ保存する表現へそろえる。</summary>
    public string Normalize(string? requestedValue)
    {
        var value = requestedValue?.Trim() ?? string.Empty;
        return Type switch
        {
            AppSettingValueType.String => NormalizeString(value),
            AppSettingValueType.Boolean => NormalizeBoolean(value),
            AppSettingValueType.Integer => NormalizeInteger(value),
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
        if (!bool.TryParse(value, out var parsed))
        {
            throw new AppSettingValidationException($"{LabelJa}は真偽値で指定してください。");
        }

        return parsed ? "true" : "false";
    }

    private string NormalizeInteger(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new AppSettingValidationException($"{LabelJa}は整数で指定してください。");
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
    TimeProvider timeProvider) : IAppSettingsProvider
{
    /// <summary>管理画面の設定区画が機能することを示す、管理者向けのお知らせ。</summary>
    public const string AdminNoticeKey = "QUESTIONNAIRE_ADMIN_NOTICE";
    public const string PleasanterBaseUrlKey = "QUESTIONNAIRE_PLEASANTER_BASEURL";
    public const string PleasanterApiVersionKey = "QUESTIONNAIRE_PLEASANTER_APIVERSION";
    public const string PleasanterTimeZoneKey = "QUESTIONNAIRE_PLEASANTER_TIMEZONE";
    public const string PleasanterApiKeyKey = "QUESTIONNAIRE_PLEASANTER_APIKEY";

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

                if (configuration[key] is { } external)
                {
                    if (string.Equals(
                        definition.Normalize(requestedValue),
                        NormalizeExternal(definition, external),
                        StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new AppSettingValidationException(
                        $"{definition.LabelJa}は外部設定で固定されているため、画面から変更できません。");
                }

                var value = definition.Normalize(requestedValue);
                await store.SaveAsync(
                    key,
                    definition.IsSecret ? secretProtector.Protect(value) : value,
                    definition.IsSecret,
                    updatedByAdminUserId,
                    cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref cache, null);
            return Cache(
                await LoadAsync(cancellationToken).ConfigureAwait(false),
                timeProvider.GetUtcNow());
        }
        finally
        {
            gate.Release();
        }
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

    private static string NormalizeExternal(AppSettingDefinition definition, string value)
    {
        try
        {
            return definition.Normalize(value);
        }
        catch (AppSettingValidationException)
            when (definition.Key == PleasanterApiVersionKey)
        {
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
