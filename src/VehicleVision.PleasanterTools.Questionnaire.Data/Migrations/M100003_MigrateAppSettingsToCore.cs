using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アプリケーション設定を、Core.Data の共有表（<c>Core_AppSettings</c>）へ写す。</summary>
/// <remarks>
/// <para>
/// 旧 <c>AppSettings</c> の行を <c>Core_AppSettings</c> へ写し、旧表を落とす（<see cref="LegacyStoreMigration"/>）。
/// </para>
/// <list type="bullet">
/// <item><description><c>IsSecret</c>（真偽値）→ <c>IsSecret</c>（整数 0 / 1）。<b>PostgreSQL だけ真偽値を <c>= true</c> で比べる</b>（<c>= 1</c> は型が合わず通らない）</description></item>
/// <item><description><c>UpdatedByAdminUserId</c> → <c>UpdatedBy</c></description></item>
/// <item><description><b>秘密の値は暗号文のまま写す</b>（復号しない）。そのほかは同名の列をそのまま写す</description></item>
/// </list>
/// </remarks>
[Migration(100003, "アプリケーション設定を Core.Data の共有表へ写す")]
public sealed class M100003_MigrateAppSettingsToCore : Migration
{
    public override void Up() =>
        LegacyStoreMigration.CopyThenDrop(this, "AppSettings", provider =>
        {
            var isSecret = provider is DatabaseProvider.PostgreSql
                ? "CASE WHEN s.[IsSecret] = true THEN 1 ELSE 0 END"
                : "CASE WHEN s.[IsSecret] = 1 THEN 1 ELSE 0 END";

            return
                "INSERT INTO [Core_AppSettings] "
                + "([SettingKey], [Value], [IsSecret], [UpdatedAt], [UpdatedBy]) "
                + $"SELECT s.[SettingKey], s.[Value], {isSecret}, s.[UpdatedAt], s.[UpdatedByAdminUserId] "
                + "FROM [AppSettings] s "
                + "WHERE NOT EXISTS (SELECT 1 FROM [Core_AppSettings] c WHERE c.[SettingKey] = "
                + LegacyStoreMigration.KeyOf(provider, "s.[SettingKey]") + ")";
        });

    /// <summary>前方のみ。旧表の形へは戻さない。</summary>
    public override void Down()
    {
    }
}
