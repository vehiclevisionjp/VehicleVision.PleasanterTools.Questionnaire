using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>自動返信メールの設定（Issue #189）。</summary>
/// <remarks>
/// <para>
/// ⚠️ **下書きは JSON ではなく表に分けて持っている**（<c>Pages</c> / <c>Questions</c> …）。
/// **定義に項目を足しただけでは保存されない。** 列が無い項目は黙って落ちる。
/// **端から端まで通す試験で、実際にメールが出ないことから見つかった。**
/// </para>
/// <para>
/// **テーマ（<c>ThemeJson</c>）と同じく JSON 1 本で持つ**（<c>M0009_SurveyTheme</c>）。
/// 件名・本文・宛先の設問…と列を足していく形にすると、
/// 設定を 1 つ増やすたびに移行が要る。
/// </para>
/// <para>
/// **NULL は「送らない」。** 触っていないアンケートは今までのまま。
/// </para>
/// <para>
/// **公開済みの版には移行が要らない。** あちらは定義まるごとの JSON なので、
/// 読むときに <c>AutoReply</c> が <c>null</c>（＝送らない）になるだけ。
/// </para>
/// </remarks>
[Migration(18, "自動返信メールの設定")]
public sealed class M0018_SurveyAutoReply : Migration
{
    public override void Up() =>
        Alter.Table("Surveys")
            .AddColumn("AutoReplyJson").AsString(int.MaxValue).Nullable();

    public override void Down() =>
        Delete.Column("AutoReplyJson").FromTable("Surveys");
}
