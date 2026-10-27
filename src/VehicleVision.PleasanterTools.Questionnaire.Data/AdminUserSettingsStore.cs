using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>回答通知メールを受け取る管理者。**秘密は含めない。**</summary>
public sealed record ResponseNotificationRecipient(
    Guid AdminUserId,
    string LoginId,
    string? Language);

/// <summary>
/// 管理者の、Core.Data の共有表（<c>Core_AdminUsers</c> など）には無い部分を扱う。
/// </summary>
/// <remarks>
/// <para>
/// 管理者そのもの・復旧コード・セッションは、Core.Data の <c>IAdminUserStore</c> / <c>IAdminSessionStore</c> が持つ。
/// ここにあるのは、<b>アンケート固有の設定</b>（回答通知メールを受け取るか）と、
/// Core の窓口に無い、Core の表への読み書きだけ。
/// </para>
/// </remarks>
public interface IAdminUserSettingsStore
{
    /// <summary>新しい回答のまとめ通知をメールで受け取るか。**既定は無効**（行が無ければ無効）。</summary>
    Task<bool> GetResponseNotificationEnabledAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>新しい回答のまとめ通知をメールで受け取るかを設定する。</summary>
    Task SetResponseNotificationEnabledAsync(
        Guid adminUserId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>回答通知メールを受け取る、有効な管理者を読む。</summary>
    Task<IReadOnlyList<ResponseNotificationRecipient>> ListResponseNotificationRecipientsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 管理画面を出す言語を決める。<c>null</c> で「選んでいない」に戻す。
    /// </summary>
    /// <remarks>Core の窓口へ渡す。「選んでいない」は空文字（列は NULL を許さない）。</remarks>
    Task SetLanguageAsync(Guid adminUserId, string? language, CancellationToken cancellationToken = default);

    /// <summary>まだ使っていない復旧コードのハッシュを返す（Core の窓口へ渡す）。</summary>
    /// <remarks>
    /// 復旧コードのハッシュは<b>塩つき（PBKDF2）で、入力から同じハッシュを作れない</b>。
    /// Core の窓口は<b>ハッシュの完全一致</b>でしか使用済みにできないので、
    /// 照合（<c>PasswordHasher.Verify</c>）のために保存済みのハッシュを読む。使用済みにするのは Core の窓口。
    /// </remarks>
    Task<IReadOnlyList<string>> ListUnusedRecoveryCodeHashesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。表は <c>AdminUserSettings</c>（M0038）。</summary>
public sealed class AdminUserSettingsStore(IDbConnectionFactory connectionFactory, IAdminUserStore users) : IAdminUserSettingsStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<bool> GetResponseNotificationEnabledAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool?>(Sql(
            "SELECT [ResponseNotificationEnabled] FROM [AdminUserSettings] WHERE [AdminUserId] = @AdminUserId",
            new { AdminUserId = adminUserId },
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? false;
    }

    public async Task SetResponseNotificationEnabledAsync(
        Guid adminUserId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var parameters = new { AdminUserId = adminUserId, Enabled = enabled };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        if (await UpdateAsync(connection, parameters, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [AdminUserSettings] ([AdminUserId], [ResponseNotificationEnabled]) "
                + "VALUES (@AdminUserId, @Enabled)",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 同時に別の要求が行を作った（主キーの重複）。**更新し直せば足りる**。更新も通らなければ握りつぶさない
            if (!await UpdateAsync(connection, parameters, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    public async Task<IReadOnlyList<ResponseNotificationRecipient>> ListResponseNotificationRecipientsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **有効な管理者だけ。** 止めた管理者へは送らない
        var rows = await connection.QueryAsync<RecipientRow>(Sql(
            "SELECT u.[Id] AS [AdminUserId], u.[LoginId], u.[Language] FROM [Core_AdminUsers] u "
            + "INNER JOIN [AdminUserSettings] s ON s.[AdminUserId] = u.[Id] "
            + "WHERE s.[ResponseNotificationEnabled] = @Enabled AND u.[IsDisabled] = 0 "
            + "ORDER BY u.[Id]",
            new { Enabled = true },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows
            .Select(row => new ResponseNotificationRecipient(
                row.AdminUserId,
                row.LoginId,
                string.IsNullOrEmpty(row.Language) ? null : row.Language))
            .ToList();
    }

    public async Task SetLanguageAsync(
        Guid adminUserId,
        string? language,
        CancellationToken cancellationToken = default) =>
        await users.SetLanguageAsync(adminUserId, language ?? string.Empty, cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<string>> ListUnusedRecoveryCodeHashesAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default) =>
        users.ListUnusedRecoveryCodeHashesAsync(adminUserId, cancellationToken);

    private async Task<bool> UpdateAsync(
        DbConnection connection,
        object parameters,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(Sql(
            "UPDATE [AdminUserSettings] SET [ResponseNotificationEnabled] = @Enabled "
            + "WHERE [AdminUserId] = @AdminUserId",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む**（<c>SqlDialect.Format</c> が各 RDBMS の引用符へ書き換える）。</summary>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed class RecipientRow
    {
        public Guid AdminUserId { get; set; }

        public string LoginId { get; set; } = "";

        public string? Language { get; set; }
    }
}
