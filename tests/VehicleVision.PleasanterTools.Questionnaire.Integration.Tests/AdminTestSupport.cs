using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>
/// 管理者まわりの結合試験が共有する部品。
/// 管理者・復旧コード・初期設定の印・セッションは Core.Data の共有表（<c>Core_</c>）にあり、
/// アンケート固有の設定は <c>AdminUserSettings</c> にある。
/// </summary>
internal static class AdminTestSupport
{
    /// <summary>
    /// 管理者に関わる表。<b>消す順序は、参照する側が先</b>
    /// （外部キーは無いが、孤児を作らない順にしておく）。
    /// </summary>
    private static readonly string[] Tables =
    [
        "Core_AdminSessions",
        "Core_AdminInvitations",
        "Core_AdminRecoveryCodes",
        "AdminUserSettings",
        "Core_AdminSetup",
        "Core_AdminUsers",
    ];

    /// <summary>管理者をすべて消す。<b>「まだ 1 人も居ない」から始めたいとき</b>に使う（初期設定の印も消す）。</summary>
    public static async Task ClearAdministratorsAsync(DbConnection connection, DatabaseProvider provider)
    {
        foreach (var table in Tables)
        {
            await connection.ExecuteAsync($"DELETE FROM {SqlDialect.Quote(provider, table)}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>管理者をすべて消す（接続は内部で開く）。</summary>
    public static async Task ClearAdministratorsAsync(DbConnectionFactory factory)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await ClearAdministratorsAsync(connection, factory.Provider).ConfigureAwait(false);
    }

    /// <summary>
    /// アプリと同じ作りの管理者ストア（Core.Data）。
    /// 共有鍵の暗号化は、本アプリの <see cref="SecretProtector"/> を合わせたアダプタ。
    /// </summary>
    public static AdminUserStore CreateUserStore(
        DbConnectionFactory factory,
        TimeProvider time,
        SecretProtector protector) =>
        new(factory, new SecretCipherAdapter(protector), time, QuestionnaireDatabase.Instance.AppId);
}
