using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>アンケートごとの proof-of-work の要否。</summary>
/// <remarks>
/// <para>
/// **アプリ全体の設定だけでは足りない**（Issue #66）。
/// 社内向けの短いアンケートでは要らない一方、公開の窓口では要る。
/// **同じ導入先に両方が同居する。**
/// </para>
/// <para>
/// **定義ではなく運用の設定なので <c>Surveys</c> に置く**
/// （<c>ResponseLimit</c> や <c>AcceptFrom</c> と同じ扱い）。
/// 定義（<c>SurveyVersions</c>）へ入れると不変のスナップショットの一部になり、
/// **切り替えるたびに公開し直す**ことになる。
/// </para>
/// <para>
/// **既定は有効。** 公開の窓口に置かれることを前提にする。
/// 既にある行にも <c>true</c> が入るので、**移行しただけで守りが緩まない。**
/// 切るのは、切ってよいと判断した人が明示的に行う。
/// </para>
/// <para>
/// **これを切っても送信チケット・最短時間・honeypot は外れない**
/// （<c>Web/Services/SubmissionGuard.cs</c>）。外れるのは proof-of-work だけ。
/// </para>
/// </remarks>
[Migration(11, "アンケートごとの proof-of-work の要否")]
public sealed class M0011_SurveyProofOfWork : Migration
{
    public override void Up() =>
        // **既定値を true にして足す。** false で足すと、
        // 移行した瞬間に既存のアンケートすべてで proof-of-work が外れる
        Alter.Table("Surveys")
            .AddColumn("RequireProofOfWork").AsBoolean().NotNullable().WithDefaultValue(true);

    public override void Down() => Delete.Column("RequireProofOfWork").FromTable("Surveys");
}
