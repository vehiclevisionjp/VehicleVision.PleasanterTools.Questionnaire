using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>DB に保存する管理画面のログイン方式の設定の生値（Issue #1056）。</summary>
/// <remarks>
/// <c>PasswordSignIn</c> は <c>"true"</c> / <c>"false"</c> / 空。**空は「指定なし」（＝隠さない）。**
/// 位置指定の record にしないこと（SELECT の並びとずれると実行時に落ちるため。SAML の設定と同じ）。
/// </remarks>
public sealed record AdminSignInSettingValues
{
    public string? PasswordSignIn { get; init; }
}

/// <summary>管理画面のログイン方式の設定を読み書きする。</summary>
public interface IAdminSignInSettingStore
{
    Task<AdminSignInSettingValues> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AdminSignInSettingValues values, CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った管理画面のログイン方式の設定の保存先。</summary>
/// <remarks>**1 行だけの表**（<c>AdminSignInSettingId = 1</c>）。行はマイグレーションで作る。</remarks>
public sealed class AdminSignInSettingStore(IDbConnectionFactory connectionFactory)
    : IAdminSignInSettingStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<AdminSignInSettingValues> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<AdminSignInSettingValues>(Sql(
            "SELECT [PasswordSignIn] FROM [AdminSignInSettings] WHERE [AdminSignInSettingId] = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        AdminSignInSettingValues values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [AdminSignInSettings] SET [PasswordSignIn] = @PasswordSignIn "
            + "WHERE [AdminSignInSettingId] = 1",
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
