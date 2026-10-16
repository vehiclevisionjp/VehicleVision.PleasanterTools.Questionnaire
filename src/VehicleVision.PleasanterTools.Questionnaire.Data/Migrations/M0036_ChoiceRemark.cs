using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>選択肢に回答者へ見せる備考を追加する（Issue #621）。</summary>
[Migration(36, "選択肢へ回答者向け備考を追加する")]
public sealed class M0036_ChoiceRemark : Migration
{
    public override void Up()
    {
        // 表示専用の多言語文言。未指定の既存選択肢は NULL のままにする
        Alter.Table("QuestionChoices")
            .AddColumn("RemarkJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("RemarkJson").FromTable("QuestionChoices");
    }
}
