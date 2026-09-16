using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>DB に保存する SAML 設定の生値。</summary>
public sealed record SamlSettingValues
{
    public string? Enabled { get; init; }

    public string? EntityId { get; init; }

    public string? IdpEntityId { get; init; }

    public string? SingleSignOnUrl { get; init; }

    public string? IdpCertificate { get; init; }

    public string? UnknownUser { get; init; }

    public string? RegisterRole { get; init; }

    public string? LoginIdSource { get; init; }

    public string? LoginIdClaim { get; init; }

    public string? ButtonLabel { get; init; }

    public string? SingleLogoutUrl { get; init; }
}

/// <summary>SAML 設定を読み書きする。</summary>
public interface ISamlSettingStore
{
    Task<SamlSettingValues> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SamlSettingValues values, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った SAML 設定の保存先。</summary>
public sealed class SamlSettingStore(IDbConnectionFactory connectionFactory) : ISamlSettingStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<SamlSettingValues> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<SamlSettingValues>(Sql(
            "SELECT [Enabled], [EntityId], [IdpEntityId], [SingleSignOnUrl], "
            + "       [IdpCertificate], [UnknownUser], [RegisterRole], [LoginIdSource], "
            + "       [LoginIdClaim], [ButtonLabel], [SingleLogoutUrl] "
            + "FROM [SamlSettings] WHERE [SamlSettingId] = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        SamlSettingValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [SamlSettings] SET "
            + "[Enabled] = @Enabled, [EntityId] = @EntityId, [IdpEntityId] = @IdpEntityId, "
            + "[SingleSignOnUrl] = @SingleSignOnUrl, [IdpCertificate] = @IdpCertificate, "
            + "[UnknownUser] = @UnknownUser, [RegisterRole] = @RegisterRole, "
            + "[LoginIdSource] = @LoginIdSource, [LoginIdClaim] = @LoginIdClaim, "
            + "[ButtonLabel] = @ButtonLabel, [SingleLogoutUrl] = @SingleLogoutUrl "
            + "WHERE [SamlSettingId] = 1",
            values,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(
            SqlDialect.Format(Provider, sql),
            parameters,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
