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
    Func<string, string>? StringNormalizer = null,
    string? BooleanFalseAlias = null)
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
            foreach (var (key, requestedValue) in values)
            {
                if (!Definitions.TryGetValue(key, out var definition))
                {
                    throw new AppSettingValidationException($"管理対象ではない設定です: {key}");
                }

                if (configuration[key] is { } external)
                {
                    if (string.Equals(
                        definition.Normalize(requestedValue),
                        definition.Normalize(external),
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
                values[definition.Key] = definition.Normalize(external);
                fixedKeys.Add(definition.Key);
                continue;
            }

            if (!records.TryGetValue(definition.Key, out var record))
            {
                values[definition.Key] = definition.Normalize(definition.DefaultValue);
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
}
