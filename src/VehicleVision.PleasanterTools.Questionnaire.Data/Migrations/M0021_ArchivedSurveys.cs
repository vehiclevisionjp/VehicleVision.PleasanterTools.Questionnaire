using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートを削除せず一覧と回答受付から畳めるようにする（Issue #233）。</summary>
[Migration(21, "アンケートのアーカイブ日時を追加する")]
public sealed class M0021_ArchivedSurveys : Migration
{
    public override void Up()
    {
        Alter.Table("Surveys")
            .AddColumn("ArchivedAt").AsDateTime2().Nullable();
    }

    public override void Down()
    {
        Delete.Column("ArchivedAt").FromTable("Surveys");
    }
}
