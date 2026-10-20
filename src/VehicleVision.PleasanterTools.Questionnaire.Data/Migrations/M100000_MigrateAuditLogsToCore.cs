using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理操作の記録を、Core.Data の共有表（<c>Core_AuditLogs</c>）へ写す。</summary>
/// <remarks>
/// <para>
/// 旧 <c>AuditLogs</c> の行を <c>Core_AuditLogs</c> へ写し、旧表を落とす（<see cref="LegacyStoreMigration"/>）。
/// 列の対応は次のとおり。
/// </para>
/// <list type="bullet">
/// <item><description><c>AdminUserId</c>（Guid）→ <c>ActorKey</c>（<b>ハイフン付き小文字の文字列</b>。Core のストアが読み書きする形）</description></item>
/// <item><description><c>ActorLoginId</c> ← 旧 <c>AdminUsers.LoginId</c>（<b>LEFT JOIN</b>。居ない管理者の行は NULL。旧は読むときに結合していた）</description></item>
/// <item><description><c>Succeeded</c> ← <c>StatusCode</c> が NULL か 400 未満なら 1、そうでなければ 0
/// （結果を持たない行は、旧の「400 以上だけを失敗」と同じ側へ倒して成功とする）</description></item>
/// <item><description>そのほかは同名の列をそのまま写す</description></item>
/// </list>
/// </remarks>
[Migration(100000, "管理操作の記録を Core.Data の共有表へ写す")]
public sealed class M100000_MigrateAuditLogsToCore : Migration
{
    public override void Up()
    {
        var hasAdminUsers = Schema.Table("AdminUsers").Exists();

        LegacyStoreMigration.CopyThenDrop(this, "AuditLogs", provider =>
        {
            // Guid を、Core のストアが書く形（ハイフン付き小文字）の文字列にする
            var actorKey = provider switch
            {
                DatabaseProvider.SqlServer => "LOWER(CAST(a.[AdminUserId] AS nvarchar(36)))",
                DatabaseProvider.PostgreSql => "CAST(a.[AdminUserId] AS text)",
                // MySQL（CHAR(36)）と SQLite（TEXT。大文字で入っている）は文字列なので、小文字へ揃える
                _ => "LOWER(a.[AdminUserId])",
            };

            // 旧は AdminUsers との LEFT JOIN で名前を出していた。表が無ければ名前は NULL
            var loginId = hasAdminUsers ? "u.[LoginId]" : "NULL";
            var join = hasAdminUsers
                ? "LEFT JOIN [AdminUsers] u ON "
                    + LegacyStoreMigration.KeyOf(provider, "u.[AdminUserId]")
                    + " = " + LegacyStoreMigration.KeyOf(provider, "a.[AdminUserId]") + " "
                : string.Empty;

            return
                "INSERT INTO [Core_AuditLogs] "
                + "([AuditLogId], [OccurredAt], [ActorKey], [ActorLoginId], [Action], [Succeeded], "
                + " [StatusCode], [TargetType], [TargetId], [DetailJson], [IpAddress]) "
                + $"SELECT a.[AuditLogId], a.[OccurredAt], {actorKey}, {loginId}, a.[Action], "
                + "CASE WHEN a.[StatusCode] IS NULL OR a.[StatusCode] < 400 THEN 1 ELSE 0 END, "
                + "a.[StatusCode], a.[TargetType], a.[TargetId], a.[DetailJson], a.[IpAddress] "
                + "FROM [AuditLogs] a "
                + join
                + "WHERE NOT EXISTS (SELECT 1 FROM [Core_AuditLogs] c WHERE c.[AuditLogId] = "
                + LegacyStoreMigration.KeyOf(provider, "a.[AuditLogId]") + ")";
        });
    }

    /// <summary>前方のみ。旧表の形へは戻さない。</summary>
    public override void Down()
    {
    }
}
