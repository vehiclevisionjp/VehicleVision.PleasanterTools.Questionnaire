using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>Pleasanter SSO の許可する所属を保存する（Issue #505）。</summary>
[Migration(33, "Pleasanter SSO の許可する組織とグループを保存する")]
public sealed class M0033_PleasanterSsoMembership : Migration
{
    public override void Up()
    {
        Alter.Table("PleasanterSsoSettings")
            .AddColumn("AllowedDeptIds").AsString(1024).Nullable()
            .AddColumn("AllowedGroupIds").AsString(1024).Nullable();
    }

    public override void Down()
    {
        Delete.Column("AllowedDeptIds").FromTable("PleasanterSsoSettings");
        Delete.Column("AllowedGroupIds").FromTable("PleasanterSsoSettings");
    }
}
