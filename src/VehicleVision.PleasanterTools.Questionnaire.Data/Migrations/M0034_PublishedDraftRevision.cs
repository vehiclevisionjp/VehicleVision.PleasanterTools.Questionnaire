using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>公開した時点の下書きの版を保存する（Issue #577）。</summary>
[Migration(34, "公開した時点の下書きの版を保存する")]
public sealed class M0034_PublishedDraftRevision : Migration
{
    public override void Up()
    {
        Alter.Table("Surveys")
            .AddColumn("PublishedDraftRevision").AsInt32().Nullable();

        // 既に公開済みの行は、移行直後に警告を出さない。
        Execute.Sql(
            "UPDATE Surveys SET PublishedDraftRevision = DraftRevision "
            + "WHERE PublishedVersion IS NOT NULL");
    }

    public override void Down()
    {
        Delete.Column("PublishedDraftRevision").FromTable("Surveys");
    }
}
