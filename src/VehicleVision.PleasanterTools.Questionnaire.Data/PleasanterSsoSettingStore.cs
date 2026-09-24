using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>DB に保存する Pleasanter シングルサインオン設定の生値（Issue #464）。</summary>
/// <remarks>
/// **位置指定の record にしないこと。** init だけのプロパティにしておけば、
/// Dapper は列名で結び付ける。位置指定にすると SELECT の並びとずれたときに実行時に落ちる。
/// </remarks>
public sealed record PleasanterSsoSettingValues
{
    public string? Enabled { get; init; }

    public string? InternalBaseUrl { get; init; }

    public string? LoginUrl { get; init; }

    public string? LogoutUrl { get; init; }

    public string? Method { get; init; }

    public string? SqlName { get; init; }

    public string? CookieNames { get; init; }

    public string? UnknownUser { get; init; }

    public string? RegisterRole { get; init; }

    public string? RevalidateMinutes { get; init; }

    public string? TimeoutSeconds { get; init; }

    public string? ButtonLabel { get; init; }
}

/// <summary>Pleasanter シングルサインオン設定を読み書きする。</summary>
public interface IPleasanterSsoSettingStore
{
    Task<PleasanterSsoSettingValues> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        PleasanterSsoSettingValues values,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った Pleasanter シングルサインオン設定の保存先。</summary>
/// <remarks>**1 行だけの表**（<c>PleasanterSsoSettingId = 1</c>）。行はマイグレーションで作る。</remarks>
public sealed class PleasanterSsoSettingStore(IDbConnectionFactory connectionFactory)
    : IPleasanterSsoSettingStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<PleasanterSsoSettingValues> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<PleasanterSsoSettingValues>(Sql(
            "SELECT [Enabled], [InternalBaseUrl], [LoginUrl], [LogoutUrl], [Method], [SqlName], "
            + "       [CookieNames], [UnknownUser], [RegisterRole], [RevalidateMinutes], "
            + "       [TimeoutSeconds], [ButtonLabel] "
            + "FROM [PleasanterSsoSettings] WHERE [PleasanterSsoSettingId] = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        PleasanterSsoSettingValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [PleasanterSsoSettings] SET "
            + "[Enabled] = @Enabled, [InternalBaseUrl] = @InternalBaseUrl, "
            + "[LoginUrl] = @LoginUrl, [LogoutUrl] = @LogoutUrl, [Method] = @Method, [SqlName] = @SqlName, "
            + "[CookieNames] = @CookieNames, [UnknownUser] = @UnknownUser, "
            + "[RegisterRole] = @RegisterRole, [RevalidateMinutes] = @RevalidateMinutes, "
            + "[TimeoutSeconds] = @TimeoutSeconds, [ButtonLabel] = @ButtonLabel "
            + "WHERE [PleasanterSsoSettingId] = 1",
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
