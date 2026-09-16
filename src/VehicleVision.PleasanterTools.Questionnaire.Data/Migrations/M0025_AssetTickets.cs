using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>回答後の配布資産に使う引換券（Issue #318）。</summary>
[Migration(25, "回答後の配布資産に使う引換券")]
public sealed class M0025_AssetTickets : Migration
{
    public override void Up()
    {
        Create.Table("AssetTickets")
            .WithColumn("TicketHash").AsString(512).NotNullable().PrimaryKey()
            .WithColumn("ResponseToken").AsString(64).NotNullable()
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        Create.Index("IX_AssetTickets_SurveyId")
            .OnTable("AssetTickets")
            .OnColumn("SurveyId").Ascending();

        Create.Index("IX_AssetTickets_ExpiresAt")
            .OnTable("AssetTickets")
            .OnColumn("ExpiresAt").Ascending();

        Alter.Table("Surveys")
            .AddColumn("AssetDeliveryJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("AssetDeliveryJson").FromTable("Surveys");
        Delete.Table("AssetTickets");
    }
}
