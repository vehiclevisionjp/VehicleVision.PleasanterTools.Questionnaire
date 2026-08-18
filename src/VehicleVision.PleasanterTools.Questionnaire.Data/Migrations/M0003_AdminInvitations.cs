using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者の招待。**2 人目以降を既定の合言葉なしで迎えるための表。**</summary>
/// <remarks>
/// <para>
/// **追加した管理者に既定の合言葉を配らない**（<c>_documents/非機能設計.md</c> 1 章）。
/// 配ると、変え忘れがそのまま残る。代わりに**期限付きで 1 回しか使えない招待**を渡し、
/// 合言葉は本人に決めさせる。
/// </para>
/// <para>
/// **トークンはハッシュのみを置く。** 表を読めた人がそのまま招待を使えては意味が無い。
/// </para>
/// </remarks>
[Migration(3, "管理者の招待")]
public sealed class M0003_AdminInvitations : Migration
{
    public override void Up()
    {
        Create.Table("AdminInvitations")
            .WithColumn("InvitationId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("AdminUserId").AsGuid().NotNullable()
            // **ハッシュのみ。** 元のトークンは発行の瞬間にしか存在しない
            .WithColumn("TokenHash").AsString(128).NotNullable().Unique()
            // **期限を必ず持つ。** 期限の無い招待は、後から拾われて使われる
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            // **使ったら印を付けて二度と通さない。** 消さずに残すのは、使われた事実を追うため
            .WithColumn("UsedAt").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("CreatedBy").AsGuid().Nullable();

        Create.Index("IX_AdminInvitations_AdminUserId")
            .OnTable("AdminInvitations").OnColumn("AdminUserId").Ascending();
    }

    public override void Down() => Delete.Table("AdminInvitations");
}
