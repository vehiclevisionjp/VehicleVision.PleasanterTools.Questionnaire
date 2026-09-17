using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>配布資料の受取履歴を送信待ちへ積む（Issue #323）。</summary>
[Migration(26, "配布資料の受取履歴")]
public sealed class M0026_AssetHistory : Migration
{
    public override void Up()
    {
        Create.Table("AssetHistoryOutbox")
            .WithColumn("EventId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("SurveyVersion").AsInt32().NotNullable()
            .WithColumn("ResponseToken").AsString(64).NotNullable()
            .WithColumn("EventType").AsInt32().NotNullable()
            .WithColumn("AssetId").AsGuid().Nullable()
            .WithColumn("AssetFileName").AsString(512).Nullable()
            .WithColumn("OccurredAt").AsDateTime2().NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RetryCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptAt").AsDateTime2().NotNullable()
            .WithColumn("LastError").AsString(1024).Nullable()
            .WithColumn("LockedBy").AsString(64).Nullable()
            .WithColumn("LockedUntil").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        Create.Index("IX_AssetHistoryOutbox_Status_NextAttemptAt")
            .OnTable("AssetHistoryOutbox")
            .OnColumn("Status").Ascending()
            .OnColumn("NextAttemptAt").Ascending();

        Create.Index("IX_AssetHistoryOutbox_SurveyId")
            .OnTable("AssetHistoryOutbox")
            .OnColumn("SurveyId").Ascending();

        Alter.Table("Surveys")
            .AddColumn("AssetHistorySiteId").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("AssetHistoryMappingJson").AsString(int.MaxValue).Nullable();

        Alter.Table("SurveyVersions")
            .AddColumn("AssetHistorySiteId").AsInt64().NotNullable().WithDefaultValue(0)
            .AddColumn("AssetHistoryMappingJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("AssetHistoryMappingJson").FromTable("SurveyVersions");
        Delete.Column("AssetHistorySiteId").FromTable("SurveyVersions");
        Delete.Column("AssetHistoryMappingJson").FromTable("Surveys");
        Delete.Column("AssetHistorySiteId").FromTable("Surveys");
        Delete.Table("AssetHistoryOutbox");
    }
}
