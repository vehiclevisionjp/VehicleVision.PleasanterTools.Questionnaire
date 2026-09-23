using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>DB に保存したアプリケーション設定。</summary>
/// <remarks>Dapper のコンストラクター割り当てに使うため、SELECT と引数の順をそろえる。</remarks>
public sealed record AppSettingRecord(
    string SettingKey,
    string Value,
    bool IsSecret,
    DateTime UpdatedAt,
    Guid UpdatedByAdminUserId);

/// <summary>アプリケーション設定を読み書きする。</summary>
public interface IAppSettingStore
{
    Task<IReadOnlyList<AppSettingRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string settingKey,
        string value,
        bool isSecret,
        Guid updatedByAdminUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使ったアプリケーション設定の保存先。</summary>
public sealed class AppSettingStore(IDbConnectionFactory connectionFactory) : IAppSettingStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<IReadOnlyList<AppSettingRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var records = await connection.QueryAsync<AppSettingRecord>(Sql(
            "SELECT [SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedByAdminUserId] "
            + "FROM [AppSettings]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return records.AsList();
    }

    public async Task SaveAsync(
        string settingKey,
        string value,
        bool isSecret,
        Guid updatedByAdminUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        ArgumentNullException.ThrowIfNull(value);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var parameters = new
        {
            SettingKey = settingKey,
            Value = value,
            IsSecret = isSecret,
            UpdatedAt = DbTime.UtcNowTruncated(),
            UpdatedByAdminUserId = updatedByAdminUserId,
        };

        await connection.ExecuteAsync(Sql(
            "DELETE FROM [AppSettings] WHERE [SettingKey] = @SettingKey",
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AppSettings] "
            + "([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedByAdminUserId]) "
            + "VALUES (@SettingKey, @Value, @IsSecret, @UpdatedAt, @UpdatedByAdminUserId)",
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(
            SqlDialect.Format(Provider, sql),
            parameters,
            transaction,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
