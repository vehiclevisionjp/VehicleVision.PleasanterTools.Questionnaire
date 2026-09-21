using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>環境変数から与えるメンテナンス設定。</summary>
public sealed record MaintenanceModeOptions(
    bool EnvironmentEnabled,
    string EnvironmentMessageJa,
    string EnvironmentMessageEn)
{
    public const string DefaultMessageJa = "ただいまメンテナンス中です";

    public const string DefaultMessageEn = "The service is currently under maintenance.";
}

/// <summary>DB に保存したメンテナンス状態。</summary>
/// <remarks>
/// ⚠️ Dapper のコンストラクター割り当てを使うため、SELECT の列順と引数順を一致させる。
/// </remarks>
public sealed record MaintenanceModeRecord(
    bool IsEnabled,
    string? MessageJa,
    string? MessageEn,
    DateTime? EnabledAt,
    Guid? EnabledByAdminUserId);

/// <summary>管理画面と受付判定へ返すメンテナンス状態。</summary>
public sealed record MaintenanceModeStatus(
    bool IsActive,
    bool EnvironmentEnabled,
    bool DatabaseEnabled,
    string MessageJa,
    string MessageEn,
    DateTime? EnabledAt,
    Guid? EnabledByAdminUserId);

/// <summary>DB のメンテナンス状態を読み書きする。</summary>
public interface IMaintenanceModeStore
{
    Task<MaintenanceModeRecord> GetAsync(CancellationToken cancellationToken = default);

    Task SetAsync(
        bool enabled,
        string? messageJa,
        string? messageEn,
        Guid adminUserId,
        DateTime changedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使ったメンテナンス状態の保存先。</summary>
public sealed class MaintenanceModeStore(IDbConnectionFactory connectionFactory)
    : IMaintenanceModeStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<MaintenanceModeRecord> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<MaintenanceModeRecord>(Sql(
            "SELECT [IsEnabled], [MessageJa], [MessageEn], [EnabledAt], "
            + "       [EnabledByAdminUserId] "
            + "FROM [MaintenanceMode] WHERE [MaintenanceModeId] = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetAsync(
        bool enabled,
        string? messageJa,
        string? messageEn,
        Guid adminUserId,
        DateTime changedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [MaintenanceMode] SET "
            + "[IsEnabled] = @Enabled, [MessageJa] = @MessageJa, [MessageEn] = @MessageEn, "
            + "[EnabledAt] = @EnabledAt, [EnabledByAdminUserId] = @EnabledByAdminUserId "
            + "WHERE [MaintenanceModeId] = 1",
            new
            {
                Enabled = enabled,
                MessageJa = Normalize(messageJa),
                MessageEn = Normalize(messageEn),
                EnabledAt = enabled ? DbTime.ForDb(changedAt) : (DateTime?)null,
                EnabledByAdminUserId = enabled ? adminUserId : (Guid?)null,
            },
            cancellationToken)).ConfigureAwait(false);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

/// <summary>環境変数と DB の状態を合わせてメンテナンス中か判定する。</summary>
public sealed class MaintenanceMode(
    IMaintenanceModeStore store,
    MaintenanceModeOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool EnvironmentEnabled => options.EnvironmentEnabled;

    /// <summary>実効状態を返す。</summary>
    public async Task<MaintenanceModeStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var database = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        return ToStatus(database);
    }

    /// <summary>受付とワーカーを止める必要があるか。</summary>
    /// <remarks>
    /// 環境変数が有効なら DB を読まない。DB 自体を保守する場合にも確実に止めるため。
    /// </remarks>
    public async Task<bool> IsActiveAsync(CancellationToken cancellationToken = default) =>
        options.EnvironmentEnabled
        || (await store.GetAsync(cancellationToken).ConfigureAwait(false)).IsEnabled;

    /// <summary>回答者へ返す実効状態を取得する。</summary>
    /// <remarks>環境変数が有効なら、DB の保守中でも応答できるよう DB を読まない。</remarks>
    public Task<MaintenanceModeStatus> GetPublicStatusAsync(
        CancellationToken cancellationToken = default) =>
        options.EnvironmentEnabled
            ? Task.FromResult(new MaintenanceModeStatus(
                true,
                true,
                false,
                options.EnvironmentMessageJa,
                options.EnvironmentMessageEn,
                null,
                null))
            : GetStatusAsync(cancellationToken);

    /// <summary>DB 側の状態を変更する。環境変数側の状態には触れない。</summary>
    public async Task<MaintenanceModeStatus> SetDatabaseAsync(
        bool enabled,
        string? messageJa,
        string? messageEn,
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await store.SetAsync(
            enabled,
            messageJa,
            messageEn,
            adminUserId,
            _time.GetUtcNow().UtcDateTime,
            cancellationToken).ConfigureAwait(false);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>回答者へ見せる文言を選ぶ。</summary>
    public static string MessageOf(MaintenanceModeStatus status, string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? status.MessageEn
            : status.MessageJa;

    private MaintenanceModeStatus ToStatus(MaintenanceModeRecord database)
    {
        var environmentWins = options.EnvironmentEnabled;
        return new MaintenanceModeStatus(
            environmentWins || database.IsEnabled,
            environmentWins,
            database.IsEnabled,
            environmentWins
                ? options.EnvironmentMessageJa
                : database.MessageJa ?? MaintenanceModeOptions.DefaultMessageJa,
            environmentWins
                ? options.EnvironmentMessageEn
                : database.MessageEn ?? MaintenanceModeOptions.DefaultMessageEn,
            database.EnabledAt,
            database.EnabledByAdminUserId);
    }
}
