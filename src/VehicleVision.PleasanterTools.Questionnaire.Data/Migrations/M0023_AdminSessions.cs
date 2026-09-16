using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者の認証状態をサーバ側で個別に失効できるようにする（Issue #276）。</summary>
[Migration(23, "管理者セッションを追加する")]
public sealed class M0023_AdminSessions : Migration
{
    public override void Up()
    {
        Create.Table("AdminSessions")
            .WithColumn("AdminSessionId").AsGuid().PrimaryKey()
            .WithColumn("AdminUserId").AsGuid().NotNullable()
                .ForeignKey("FK_AdminSessions_AdminUsers", "AdminUsers", "AdminUserId")
                .OnDelete(System.Data.Rule.Cascade)
            .WithColumn("Kind").AsInt32().NotNullable()
            .WithColumn("ProtectedPayload").AsString(int.MaxValue).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("IpAddress").AsString(64).Nullable()
            .WithColumn("UserAgent").AsString(512).Nullable();

        Create.Index("IX_AdminSessions_AdminUserId_Kind_CreatedAt")
            .OnTable("AdminSessions")
            .OnColumn("AdminUserId").Ascending()
            .OnColumn("Kind").Ascending()
            .OnColumn("CreatedAt").Descending();

        Create.Index("IX_AdminSessions_ExpiresAt")
            .OnTable("AdminSessions")
            .OnColumn("ExpiresAt").Ascending();
    }

    public override void Down()
    {
        Delete.Table("AdminSessions");
    }
}
