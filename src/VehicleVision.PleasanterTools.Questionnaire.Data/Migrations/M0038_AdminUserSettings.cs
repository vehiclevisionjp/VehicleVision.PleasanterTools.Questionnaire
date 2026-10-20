using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者ごとの、アンケート固有の設定の表を作る（Issue #687）。</summary>
/// <remarks>
/// <para>
/// 管理者そのものは Core.Data の共有表（<c>Core_AdminUsers</c>）へ移る（M100004）。
/// 共有表に無い<b>回答通知メールを受け取るか</b>（旧 <c>AdminUsers.ResponseNotificationEnabled</c>）を、この表で持つ。
/// 行が無い管理者は「受け取らない」（<b>既定は無効</b>）。
/// </para>
/// <para>
/// <b>Core の表へ外部キーは張らない</b>（Core は他の表からの参照を前提にしない）。
/// 管理者を消す操作は無い（止めるだけ）ので、孤児は出ない。
/// </para>
/// <para>
/// 旧の値を写すのは M100004（旧 <c>AdminUsers</c> を落とす移行）。
/// </para>
/// </remarks>
[Migration(38, "管理者ごとのアンケート固有の設定の表を追加する")]
public sealed class M0038_AdminUserSettings : Migration
{
    public override void Up()
    {
        Create.Table("AdminUserSettings")
            .WithColumn("AdminUserId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("ResponseNotificationEnabled").AsBoolean().NotNullable().WithDefaultValue(false);

        // MySQL は、Core の表（Core_AdminUsers）を utf8mb4_0900_as_cs へ直してある。
        // 管理者 ID（CHAR(36)）で結合するので、この表も同じ文字セット・照合順序へ揃える
        // （揃えないと「Illegal mix of collations」で結合が通らない）。
        // RDBMS は IfDatabase ではなく、実際に繋いでいる接続の型で見分ける（LegacyStoreMigration の注意）
        LegacyStoreMigration.WithConnection(this, (connection, transaction, provider) =>
        {
            if (provider is DatabaseProvider.MySql)
            {
                LegacyStoreMigration.ExecuteNonQuery(
                    connection,
                    transaction,
                    provider,
                    "ALTER TABLE [AdminUserSettings] CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");
            }
        });
    }

    public override void Down() => Delete.Table("AdminUserSettings");
}
