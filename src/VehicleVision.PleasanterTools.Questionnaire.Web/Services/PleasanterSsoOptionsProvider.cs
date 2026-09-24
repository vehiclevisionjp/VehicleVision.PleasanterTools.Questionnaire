using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>外部設定を優先し、残りを DB と既定値から組み立てた Pleasanter シングルサインオン設定。</summary>
public sealed record PleasanterSsoOptionsSnapshot(
    PleasanterSsoOptions Options,
    PleasanterSsoSettingValues Values,
    IReadOnlySet<string> FixedKeys);

/// <summary>Pleasanter シングルサインオン設定を解決する（Issue #464）。</summary>
public interface IPleasanterSsoOptionsProvider
{
    Task<PleasanterSsoOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>保存せずに、画面から来た値で組み立てる（接続の試験用）。</summary>
    /// <param name="forceEnabled">有効・無効の指定に関わらず、有効として組み立てる。</param>
    Task<PleasanterSsoOptionsSnapshot> PreviewAsync(
        PleasanterSsoSettingValues values,
        bool forceEnabled,
        CancellationToken cancellationToken = default);

    Task<PleasanterSsoOptionsSnapshot> SaveAsync(
        PleasanterSsoSettingValues values,
        CancellationToken cancellationToken = default);
}

/// <summary>外部設定 → DB → 既定値の順で設定を読む。**SAML と同じ順序。**</summary>
/// <remarks>
/// <para>
/// **外部設定に値がある項目は画面から変えられない**（固定項目）。DB の値は消さずに残すので、
/// 外部設定を外すと再び使われる。
/// </para>
/// <para>
/// **30 秒だけ覚える。** ログイン画面の状態取得と再検証が要求ごとに読むため、
/// 毎回 DB を引かない。保存した直後は覚えた値を捨てる（このインスタンスでは即時反映、
/// ほかのインスタンスでも 30 秒以内に反映）。
/// </para>
/// </remarks>
public sealed class PleasanterSsoOptionsProvider(
    IConfiguration configuration,
    IPleasanterSsoSettingStore store,
    TimeProvider timeProvider,
    ILogger<PleasanterSsoOptionsProvider> logger) : IPleasanterSsoOptionsProvider
{
    /// <summary>覚えておく長さ。<see cref="AppSettingsProvider.CacheLifetime"/> とそろえる。</summary>
    public static readonly TimeSpan CacheLifetime = AppSettingsProvider.CacheLifetime;

    private static readonly (string Key, Func<PleasanterSsoSettingValues, string?> Read)[] Fields =
    [
        (PleasanterSsoOptions.EnabledKey, values => values.Enabled),
        (PleasanterSsoOptions.InternalBaseUrlKey, values => values.InternalBaseUrl),
        (PleasanterSsoOptions.LoginUrlKey, values => values.LoginUrl),
        (PleasanterSsoOptions.LogoutUrlKey, values => values.LogoutUrl),
        (PleasanterSsoOptions.SqlNameKey, values => values.SqlName),
        (PleasanterSsoOptions.CookieNamesKey, values => values.CookieNames),
        (PleasanterSsoOptions.UnknownUserKey, values => values.UnknownUser),
        (PleasanterSsoOptions.RegisterRoleKey, values => values.RegisterRole),
        (PleasanterSsoOptions.RevalidateMinutesKey, values => values.RevalidateMinutes),
        (PleasanterSsoOptions.TimeoutSecondsKey, values => values.TimeoutSeconds),
        (PleasanterSsoOptions.ButtonLabelKey, values => values.ButtonLabel),
    ];

    private CacheEntry? cache;

    public async Task<PleasanterSsoOptionsSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (Volatile.Read(ref cache) is { } cached && cached.ExpiresAt > now)
        {
            return cached.Snapshot;
        }

        var database = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        PleasanterSsoOptionsSnapshot snapshot;
        try
        {
            snapshot = Resolve(database);
        }
        catch (InvalidOperationException exception)
        {
            // **読めない設定では通さない（無効として扱う）。** 保存時に検証しているので、
            // ここへ来るのは DB を直接書き換えたときなど。**管理画面ごと 500 にはしない**
            logger.LogError(
                exception,
                "Pleasanter のシングルサインオン設定を読めないため、無効として扱います。");
            snapshot = new PleasanterSsoOptionsSnapshot(
                new PleasanterSsoOptions(),
                database,
                Fields.Where(field => configuration[field.Key] is not null)
                    .Select(field => field.Key)
                    .ToHashSet(StringComparer.Ordinal));
        }

        Volatile.Write(ref cache, new CacheEntry(snapshot, now + CacheLifetime));
        return snapshot;
    }

    public async Task<PleasanterSsoOptionsSnapshot> PreviewAsync(
        PleasanterSsoSettingValues values,
        bool forceEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var current = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(Merge(values, current), forceEnabled);
    }

    public async Task<PleasanterSsoOptionsSnapshot> SaveAsync(
        PleasanterSsoSettingValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var current = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        var saved = Merge(values, current);

        // **保存前に有効な設定か確かめる。** 壊れた値を DB へ残して認証を止めない。
        var snapshot = Resolve(saved);
        await store.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref cache, new CacheEntry(snapshot, timeProvider.GetUtcNow() + CacheLifetime));
        return snapshot;
    }

    /// <summary>固定項目は DB の現在値を残し、それ以外を画面の値で置き換える。</summary>
    private PleasanterSsoSettingValues Merge(
        PleasanterSsoSettingValues requested,
        PleasanterSsoSettingValues current) => new()
        {
            Enabled = Mutable(PleasanterSsoOptions.EnabledKey, requested.Enabled, current.Enabled),
            InternalBaseUrl = Mutable(
                PleasanterSsoOptions.InternalBaseUrlKey, requested.InternalBaseUrl, current.InternalBaseUrl),
            LoginUrl = Mutable(PleasanterSsoOptions.LoginUrlKey, requested.LoginUrl, current.LoginUrl),
            LogoutUrl = Mutable(PleasanterSsoOptions.LogoutUrlKey, requested.LogoutUrl, current.LogoutUrl),
            SqlName = Mutable(PleasanterSsoOptions.SqlNameKey, requested.SqlName, current.SqlName),
            CookieNames = Mutable(
                PleasanterSsoOptions.CookieNamesKey, requested.CookieNames, current.CookieNames),
            UnknownUser = Mutable(
                PleasanterSsoOptions.UnknownUserKey, requested.UnknownUser, current.UnknownUser),
            RegisterRole = Mutable(
                PleasanterSsoOptions.RegisterRoleKey, requested.RegisterRole, current.RegisterRole),
            RevalidateMinutes = Mutable(
                PleasanterSsoOptions.RevalidateMinutesKey, requested.RevalidateMinutes, current.RevalidateMinutes),
            TimeoutSeconds = Mutable(
                PleasanterSsoOptions.TimeoutSecondsKey, requested.TimeoutSeconds, current.TimeoutSeconds),
            ButtonLabel = Mutable(
                PleasanterSsoOptions.ButtonLabelKey, requested.ButtonLabel, current.ButtonLabel),
        };

    private PleasanterSsoOptionsSnapshot Resolve(
        PleasanterSsoSettingValues database,
        bool forceEnabled = false)
    {
        var fixedKeys = Fields
            .Where(field => configuration[field.Key] is not null)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);

        string? ValueOf(string key) =>
            configuration[key] ?? Fields.First(field => field.Key == key).Read(database);

        static string OrDefault(string? value, string defaultValue) =>
            string.IsNullOrWhiteSpace(value) ? defaultValue : value;

        var effective = new PleasanterSsoSettingValues
        {
            Enabled = forceEnabled ? "true" : OrDefault(ValueOf(PleasanterSsoOptions.EnabledKey), "false"),
            InternalBaseUrl = ValueOf(PleasanterSsoOptions.InternalBaseUrlKey) ?? string.Empty,
            LoginUrl = ValueOf(PleasanterSsoOptions.LoginUrlKey) ?? string.Empty,
            LogoutUrl = ValueOf(PleasanterSsoOptions.LogoutUrlKey) ?? string.Empty,
            SqlName = OrDefault(ValueOf(PleasanterSsoOptions.SqlNameKey), PleasanterSsoOptions.DefaultSqlName),
            CookieNames = OrDefault(
                ValueOf(PleasanterSsoOptions.CookieNamesKey), PleasanterSsoOptions.DefaultCookieNames),
            UnknownUser = OrDefault(
                ValueOf(PleasanterSsoOptions.UnknownUserKey), nameof(PleasanterSsoUnknownUserPolicy.Reject)),
            RegisterRole = OrDefault(ValueOf(PleasanterSsoOptions.RegisterRoleKey), nameof(AdminRole.Editor)),
            RevalidateMinutes = OrDefault(
                ValueOf(PleasanterSsoOptions.RevalidateMinutesKey),
                PleasanterSsoOptions.DefaultRevalidateMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            TimeoutSeconds = OrDefault(
                ValueOf(PleasanterSsoOptions.TimeoutSecondsKey),
                PleasanterSsoOptions.DefaultTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ButtonLabel = ValueOf(PleasanterSsoOptions.ButtonLabelKey) ?? string.Empty,
        };

        return new PleasanterSsoOptionsSnapshot(
            PleasanterSsoOptions.FromValues(key => Fields.First(field => field.Key == key).Read(effective)),
            effective,
            fixedKeys);
    }

    private string? Mutable(string key, string? requested, string? current) =>
        configuration[key] is null ? requested?.Trim() : current;

    private sealed record CacheEntry(PleasanterSsoOptionsSnapshot Snapshot, DateTimeOffset ExpiresAt);
}
