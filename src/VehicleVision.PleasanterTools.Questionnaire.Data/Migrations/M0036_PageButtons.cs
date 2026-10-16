using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

[Migration(36, "ページ下部の釦設定")]
public sealed class M0036_PageButtons : Migration
{
    public override void Up()
    {
        Alter.Table("Pages")
            .AddColumn("ButtonsJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("ButtonsJson").FromTable("Pages");
    }
}
