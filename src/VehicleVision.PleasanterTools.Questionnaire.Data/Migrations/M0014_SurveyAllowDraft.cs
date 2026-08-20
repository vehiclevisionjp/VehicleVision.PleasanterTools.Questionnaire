using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートごとの下書き保存の可否（Issue #59）。</summary>
/// <remarks>
/// <para>
/// **下書きは端末の中だけに置く**（2026-08-20 決定）。サーバへは送らない。
/// 送ると、送信前の回答が完全匿名の前提のまま DB に溜まることになる。
/// </para>
/// <para>
/// ⚠️ **既定は無効。** 端末は共有され得る（店頭のタブレット、社内の共用 PC）。
/// **黙って端末へ残すと、次に使う人が前の人の回答を見る。**
/// 「長いので途中で保存したい」と判断した人が、アンケート単位で入れる。
/// </para>
/// <para>
/// **定義ではなく運用の設定なので <c>Surveys</c> に置く**
/// （<c>RequireProofOfWork</c> と同じ扱い）。
/// 定義（<c>SurveyVersions</c>）へ入れると不変のスナップショットの一部になり、
/// **切り替えるたびに公開し直す**ことになる。
/// </para>
/// </remarks>
[Migration(14, "アンケートごとの下書き保存の可否")]
public sealed class M0014_SurveyAllowDraft : Migration
{
    public override void Up() =>
        // **既定値を false にして足す。** 移行しただけで
        // 既存のアンケートが端末へ回答を残し始めてはいけない
        Alter.Table("Surveys")
            .AddColumn("AllowDraft").AsBoolean().NotNullable().WithDefaultValue(false);

    public override void Down() => Delete.Column("AllowDraft").FromTable("Surveys");
}
