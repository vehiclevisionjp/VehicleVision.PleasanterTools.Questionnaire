using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートごとの回答画面埋め込み可否（Issue #334）。</summary>
/// <remarks>
/// **公開スナップショットではなく可変のアンケート行へ置く。**
/// 埋め込みを止めるために再公開を要求せず、その場で無効にできるようにする。
/// </remarks>
[Migration(27, "アンケートごとの回答画面埋め込み可否")]
public sealed class M0027_EmbeddableSurveys : Migration
{
    public override void Up() =>
        Alter.Table("Surveys")
            .AddColumn("AllowEmbedding").AsBoolean().NotNullable().WithDefaultValue(false);

    public override void Down() => Delete.Column("AllowEmbedding").FromTable("Surveys");
}
