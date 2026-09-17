using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>外部設定を優先し、残りを DB と既定値から組み立てた SAML 設定。</summary>
public sealed record SamlOptionsSnapshot(
    SamlOptions Options,
    SamlSettingValues Values,
    IReadOnlySet<string> FixedKeys);

/// <summary>SAML 設定を要求ごとに解決する。</summary>
public interface ISamlOptionsProvider
{
    Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default);

    Task<SamlOptionsSnapshot> SaveAsync(
        SamlSettingValues values,
        CancellationToken cancellationToken = default);
}

/// <summary>外部設定 → DB → 既定値の順で SAML 設定を読む。</summary>
public sealed class SamlOptionsProvider(
    IConfiguration configuration,
    ISamlSettingStore store) : ISamlOptionsProvider
{
    private static readonly (string Key, Func<SamlSettingValues, string?> Read)[] Fields =
    [
        (SamlOptions.EnabledKey, values => values.Enabled),
        (SamlOptions.EntityIdKey, values => values.EntityId),
        (SamlOptions.IdpEntityIdKey, values => values.IdpEntityId),
        (SamlOptions.SingleSignOnUrlKey, values => values.SingleSignOnUrl),
        (SamlOptions.IdpCertificateKey, values => values.IdpCertificate),
        (SamlOptions.UnknownUserKey, values => values.UnknownUser),
        (SamlOptions.RegisterRoleKey, values => values.RegisterRole),
        (SamlOptions.LoginIdSourceKey, values => values.LoginIdSource),
        (SamlOptions.LoginIdClaimKey, values => values.LoginIdClaim),
        (SamlOptions.ButtonLabelKey, values => values.ButtonLabel),
        (SamlOptions.SingleLogoutUrlKey, values => values.SingleLogoutUrl),
    ];

    public async Task<SamlOptionsSnapshot> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(database);
    }

    public async Task<SamlOptionsSnapshot> SaveAsync(
        SamlSettingValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var current = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        var saved = new SamlSettingValues
        {
            Enabled = Mutable(SamlOptions.EnabledKey, values.Enabled, current.Enabled),
            EntityId = Mutable(SamlOptions.EntityIdKey, values.EntityId, current.EntityId),
            IdpEntityId = Mutable(SamlOptions.IdpEntityIdKey, values.IdpEntityId, current.IdpEntityId),
            SingleSignOnUrl = Mutable(
                SamlOptions.SingleSignOnUrlKey, values.SingleSignOnUrl, current.SingleSignOnUrl),
            IdpCertificate = Mutable(
                SamlOptions.IdpCertificateKey, values.IdpCertificate, current.IdpCertificate),
            UnknownUser = Mutable(
                SamlOptions.UnknownUserKey, values.UnknownUser, current.UnknownUser),
            RegisterRole = Mutable(
                SamlOptions.RegisterRoleKey, values.RegisterRole, current.RegisterRole),
            LoginIdSource = Mutable(
                SamlOptions.LoginIdSourceKey, values.LoginIdSource, current.LoginIdSource),
            LoginIdClaim = Mutable(
                SamlOptions.LoginIdClaimKey, values.LoginIdClaim, current.LoginIdClaim),
            ButtonLabel = Mutable(
                SamlOptions.ButtonLabelKey, values.ButtonLabel, current.ButtonLabel),
            SingleLogoutUrl = Mutable(
                SamlOptions.SingleLogoutUrlKey, values.SingleLogoutUrl, current.SingleLogoutUrl),
        };

        // **保存前に有効な設定か確かめる。** 壊れた値を DB へ残して認証を止めない。
        var snapshot = Resolve(saved);
        await store.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private SamlOptionsSnapshot Resolve(SamlSettingValues database)
    {
        var fixedKeys = Fields
            .Where(field => configuration[field.Key] is not null)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.Ordinal);

        string? ValueOf(string key)
        {
            var external = configuration[key];
            if (external is not null)
            {
                return external;
            }

            return Fields.First(field => field.Key == key).Read(database);
        }

        var effective = new SamlSettingValues
        {
            Enabled = ValueOf(SamlOptions.EnabledKey) ?? "false",
            EntityId = ValueOf(SamlOptions.EntityIdKey) ?? string.Empty,
            IdpEntityId = ValueOf(SamlOptions.IdpEntityIdKey) ?? string.Empty,
            SingleSignOnUrl = ValueOf(SamlOptions.SingleSignOnUrlKey) ?? string.Empty,
            IdpCertificate = ValueOf(SamlOptions.IdpCertificateKey) ?? string.Empty,
            UnknownUser = ValueOf(SamlOptions.UnknownUserKey) ?? nameof(SamlUnknownUserPolicy.Reject),
            RegisterRole = ValueOf(SamlOptions.RegisterRoleKey) ?? nameof(AdminRole.Editor),
            LoginIdSource = ValueOf(SamlOptions.LoginIdSourceKey) ?? nameof(SamlLoginIdSource.NameId),
            LoginIdClaim = ValueOf(SamlOptions.LoginIdClaimKey) ?? string.Empty,
            ButtonLabel = ValueOf(SamlOptions.ButtonLabelKey) ?? string.Empty,
            SingleLogoutUrl = ValueOf(SamlOptions.SingleLogoutUrlKey) ?? string.Empty,
        };

        return new SamlOptionsSnapshot(
            SamlOptions.FromValues(key => Fields.First(field => field.Key == key).Read(effective)),
            effective,
            fixedKeys);
    }

    private string? Mutable(string key, string? requested, string? current) =>
        configuration[key] is null ? requested?.Trim() : current;
}
