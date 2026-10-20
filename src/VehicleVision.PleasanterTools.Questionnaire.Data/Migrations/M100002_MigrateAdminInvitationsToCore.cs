using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者の招待を、Core.Data の共有表（<c>Core_AdminInvitations</c>）へ写す。</summary>
/// <remarks>
/// <para>
/// 旧 <c>AdminInvitations</c> の行を <c>Core_AdminInvitations</c> へ写し、旧表を落とす（<see cref="LegacyStoreMigration"/>）。
/// 列は同名で、そのまま写す。<b>使用済みの行も写す</b>（使われた事実として残す）。
/// </para>
/// <para>
/// <b><c>TokenHash</c> は旧が 128 文字の列、Core は 64 文字の列。</b>
/// 本アプリが置くのは SHA-256 の Base64（44 文字）なので収まる。
/// 64 文字を超える値が入っていれば、SQL Server・PostgreSQL・MySQL は桁溢れで失敗し、旧表は落ちない（黙って欠けない）。
/// </para>
/// </remarks>
[Migration(100002, "管理者の招待を Core.Data の共有表へ写す")]
public sealed class M100002_MigrateAdminInvitationsToCore : Migration
{
    public override void Up() =>
        LegacyStoreMigration.CopyThenDrop(this, "AdminInvitations", provider =>
            "INSERT INTO [Core_AdminInvitations] "
            + "([InvitationId], [AdminUserId], [TokenHash], [ExpiresAt], [UsedAt], [CreatedAt], [CreatedBy]) "
            + "SELECT i.[InvitationId], i.[AdminUserId], i.[TokenHash], i.[ExpiresAt], i.[UsedAt], i.[CreatedAt], i.[CreatedBy] "
            + "FROM [AdminInvitations] i "
            + "WHERE NOT EXISTS (SELECT 1 FROM [Core_AdminInvitations] c WHERE c.[InvitationId] = "
            + LegacyStoreMigration.KeyOf(provider, "i.[InvitationId]") + ")");

    /// <summary>前方のみ。旧表の形へは戻さない。</summary>
    public override void Down()
    {
    }
}
