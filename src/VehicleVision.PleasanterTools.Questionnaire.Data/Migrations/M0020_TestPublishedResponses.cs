using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>テスト公開中に受け付けた回答を本番回答と分ける（Issue #223）。</summary>
[Migration(20, "テスト公開中の回答を識別する")]
public sealed class M0020_TestPublishedResponses : Migration
{
    public override void Up()
    {
        Alter.Table("Responses")
            .AddColumn("IsTest").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("ResponseTokens")
            .AddColumn("IsTest").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("IsTest").FromTable("ResponseTokens");
        Delete.Column("IsTest").FromTable("Responses");
    }
}
