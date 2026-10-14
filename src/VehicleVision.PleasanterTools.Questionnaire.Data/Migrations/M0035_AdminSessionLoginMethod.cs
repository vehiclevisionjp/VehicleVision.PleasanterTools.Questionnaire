using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者セッションへログインに使った方式を持たせる（Issue #602）。</summary>
[Migration(35, "管理者セッションへログイン方式を追加する")]
public sealed class M0035_AdminSessionLoginMethod : Migration
{
    public override void Up()
    {
        // 既存の行は方式が分からないので NULL のままにする（画面は「不明」と出す）
        Alter.Table("AdminSessions")
            .AddColumn("LoginMethod").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Column("LoginMethod").FromTable("AdminSessions");
    }
}
