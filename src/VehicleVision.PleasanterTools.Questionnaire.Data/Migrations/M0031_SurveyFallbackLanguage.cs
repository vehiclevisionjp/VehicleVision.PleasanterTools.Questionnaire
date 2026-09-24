using FluentMigrator;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートごとの文言の落とし先言語（Issue #432）。</summary>
[Migration(31, "アンケートごとの文言の落とし先言語")]
public sealed class M0031_SurveyFallbackLanguage : Migration
{
    public override void Up() =>
        Alter.Table("Surveys")
            .AddColumn("FallbackLanguage")
            .AsString(8)
            .NotNullable()
            .WithDefaultValue(LocalizedText.DefaultLanguage);

    public override void Down() =>
        Delete.Column("FallbackLanguage").FromTable("Surveys");
}
