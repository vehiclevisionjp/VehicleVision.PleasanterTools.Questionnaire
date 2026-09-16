using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>本文用資産の枚数を競合なく制限できるようにする（Issue #266）。</summary>
[Migration(22, "本文用資産の枚数を追加する")]
public sealed class M0022_ContentAssets : Migration
{
    public override void Up()
    {
        Alter.Table("Surveys")
            .AddColumn("ContentAssetCount").AsInt32().NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        Delete.Column("ContentAssetCount").FromTable("Surveys");
    }
}
