using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>ページの中の設問を回答者ごとに入れ替えるかどうか（Issue #103）。</summary>
/// <remarks>
/// <para>
/// **選択肢の入れ替え（<c>ShuffleChoices</c>）は移行が要らない。**
/// あちらは設問の <c>SettingsJson</c> に入るため、列を足さずに済んでいる。
/// **設問の入れ替えはページが持つ**ので、<c>Pages</c> へ列を足す。
/// </para>
/// <para>
/// **既定は「入れ替えない」。** 今あるアンケートの見え方を変えない。
/// </para>
/// <para>
/// **公開済みの版には移行が要らない。** あちらは定義まるごとの JSON なので、
/// 読むときに <c>false</c>（＝入れ替えない）になるだけ。
/// </para>
/// </remarks>
[Migration(16, "ページの設問の並べ替え")]
public sealed class M0016_ShuffleQuestions : Migration
{
    public override void Up()
    {
        // **NOT NULL ＋ 既定 false。** 「指定していない」と「入れ替えない」を
        // 区別する必要がないので、NULL を持たせない
        Alter.Table("Pages")
            .AddColumn("ShuffleQuestions").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("ShuffleQuestions").FromTable("Pages");
    }
}
