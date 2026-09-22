using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理画面から変更できるアプリケーション設定の保管先（Issue #372）。</summary>
[Migration(30, "アプリケーション設定を保存する")]
public sealed class M0030_AppSettings : Migration
{
    public override void Up()
    {
        Create.Table("AppSettings")
            .WithColumn("SettingKey").AsString(256).NotNullable().PrimaryKey()
            .WithColumn("Value").AsString(int.MaxValue).NotNullable()
            .WithColumn("IsSecret").AsBoolean().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedByAdminUserId").AsGuid().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("AppSettings");
    }
}
