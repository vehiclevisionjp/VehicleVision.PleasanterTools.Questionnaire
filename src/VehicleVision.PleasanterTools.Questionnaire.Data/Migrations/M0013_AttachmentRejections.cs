using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>添付の検査で弾いた記録を残す表（Issue #39）。</summary>
/// <remarks>
/// <para>
/// **管理操作の記録（<c>AuditLogs</c>）とは別の表にする。**
/// あちらは <c>IpAddress</c> を持つ。**弾いた記録は回答者側の出来事**なので、
/// 同じ表に入れると「回答者を完全匿名にする」前提と衝突する
/// （<c>_documents/アーキテクチャ方針.md</c>）。
/// **列そのものを作らないことで、後から足される余地を消す。**
/// </para>
/// <para>
/// **回答本文もファイル名も入れない。** ファイル名には氏名が入り得る
/// （「履歴書_山田太郎.pdf」）。**残すのは、いつ・どのアンケートで・
/// どの理由で・何件**だけ。
/// </para>
/// <para>
/// **1 回の送信につき、理由ごとに 1 行。** 1 件ずつ入れると、
/// 添付を並べて送るだけで行を好きなだけ増やせる。
/// </para>
/// </remarks>
[Migration(13, "添付の検査で弾いた記録を残す")]
public sealed class M0013_AttachmentRejections : Migration
{
    public override void Up()
    {
        Create.Table("AttachmentRejections")
            .WithColumn("AttachmentRejectionId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("OccurredAt").AsDateTime2().NotNullable()
            // **どのアンケートで起きたか。** 設定を直すのはアンケート単位なので要る
            .WithColumn("SurveyId").AsGuid().NotNullable()
            // どの設問か。**設問に紐づかない理由（合計サイズ超過など）では NULL**
            .WithColumn("QuestionId").AsString(128).Nullable()
            .WithColumn("Reason").AsInt32().NotNullable()
            // **その送信で、その理由に当たった件数**
            .WithColumn("FileCount").AsInt32().NotNullable();

        // **新しい順に読む。** 「今どうなっているか」を見るための表
        Create.Index("IX_AttachmentRejections_OccurredAt")
            .OnTable("AttachmentRejections").OnColumn("OccurredAt").Descending();
    }

    public override void Down() => Delete.Table("AttachmentRejections");
}
