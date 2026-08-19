using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートのテンプレート。</summary>
/// <remarks>
/// <para>
/// **よくある形を毎回作り直さずに済ませるためのもの**（Issue #58）。
/// 複製（Issue #46）と同じく設問・選択肢・分岐・マッピングを写すが、
/// **テンプレートは Pleasanter のサイトを持たない**ので、複製と同じ扱いにはできない。
/// </para>
/// <para>
/// **表を分けず、<c>Surveys</c> の旗で表す。** テンプレートが持つものは
/// アンケートの下書きとまったく同じ（ページ・設問・選択肢・分岐・マッピング）で、
/// 表を分けると同じ形の表が 6 つ増え、**書き込む道が 2 本になる。**
/// 道が 2 本あると、後から足した項目をテンプレート側だけ写し漏らす。
/// </para>
/// <para>
/// **サイト ID は 0 を入れる**（列は <c>NOT NULL</c> のまま）。
/// テンプレートは回答を受け付けないので書き込み先を持たず、
/// **そこから作るときに指定させる。** 列を <c>NULL</c> 可にすると、
/// 回答を書き込む経路すべてで「サイトが無い」を持ち回ることになる。
/// **テンプレートを公開できないことは入口側で断っている**
/// （<c>AdminSurveyEndpoints</c>）。
/// </para>
/// <para>
/// **公開用 ID は普通のアンケートと同じく作る。** 列が一意かつ <c>NOT NULL</c> であり、
/// 公開しないので誰にも渡らない。**そこから作るアンケートには作り直す**
/// （<c>_documents/データモデル設計.md</c> 3 章）。
/// </para>
/// </remarks>
[Migration(8, "アンケートのテンプレート")]
public sealed class M0008_SurveyTemplates : Migration
{
    public override void Up() =>
        // **既にある行はすべてアンケート。** 既定値で false が入る
        Alter.Table("Surveys")
            .AddColumn("IsTemplate").AsBoolean().NotNullable().WithDefaultValue(false);

    public override void Down() => Delete.Column("IsTemplate").FromTable("Surveys");
}
