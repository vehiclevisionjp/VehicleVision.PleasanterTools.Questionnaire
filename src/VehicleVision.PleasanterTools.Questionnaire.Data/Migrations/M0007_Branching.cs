using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>回答に応じたページ分岐と設問の出し分け。</summary>
/// <remarks>
/// <para>
/// **ジャンプ方式**（Issue #41）。選択肢ごとの行き先とページ末尾の行き先で
/// ページを飛ばし、設問側の表示条件で同じページの中を出し分ける。
/// </para>
/// <para>
/// **JSON で持つ。** 行き先も表示条件も**形が育つ**ので、列に開くと
/// 条件を 1 つ足すたびに移行が要る（<c>SettingsJson</c> と同じ考え）。
/// </para>
/// <para>
/// **公開済みの版（<c>SurveyVersions.DefinitionJson</c>）には移行が要らない。**
/// あちらは定義まるごとの JSON なので、読むときに既定値が入るだけ。
/// </para>
/// </remarks>
[Migration(7, "回答に応じたページ分岐と設問の出し分け")]
public sealed class M0007_Branching : Migration
{
    public override void Up()
    {
        // ページを終えたときの行き先。**NULL は「次のページへ」**
        Alter.Table("Pages")
            .AddColumn("NextJson").AsString(int.MaxValue).Nullable();

        // 設問を出す条件。**NULL は「常に出す」**
        Alter.Table("Questions")
            .AddColumn("VisibleWhenJson").AsString(int.MaxValue).Nullable();

        // その選択肢を選んだときの行き先。**NULL はページ末尾の行き先に従う**
        Alter.Table("QuestionChoices")
            .AddColumn("NextJson").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        Delete.Column("NextJson").FromTable("QuestionChoices");
        Delete.Column("VisibleWhenJson").FromTable("Questions");
        Delete.Column("NextJson").FromTable("Pages");
    }
}
