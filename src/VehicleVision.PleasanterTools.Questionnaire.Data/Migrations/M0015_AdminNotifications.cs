using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者への知らせを溜める表（Issue #80）。</summary>
/// <remarks>
/// <para>
/// **Pleasanter を経由しない。** 401 と滞留は**まさに Pleasanter へ届かない事象**で、
/// 届かない相手に知らせを預けられない
/// （<c>_documents/アーキテクチャ方針.md</c> 15 章の決定 5）。
/// </para>
/// <para>
/// ⚠️ **1 件ずつ行を作らない。** Pleasanter が壊れていれば回答は全部デッドレターになる。
/// **未読の同じ種類（＋アンケート）があれば、件数と時刻を足す**ので、
/// 1 種類につき未読は 1 行に集まる。
/// </para>
/// <para>
/// **回答本文・資格情報・<c>ResponseToken</c>・回答者の送信元を入れない**
/// （監査ログと同じ決まり）。**列そのものを作らないことで、後から足される余地を消す。**
/// </para>
/// <para>
/// <c>SurveyId</c> は**アンケートに紐づかない知らせでは <c>Guid.Empty</c>**。
/// NULL にすると「未読の同じ種類」を突き合わせる条件が RDBMS ごとに割れる
/// （NULL 同士は等しくない）。
/// </para>
/// </remarks>
[Migration(15, "管理者への知らせを溜める")]
public sealed class M0015_AdminNotifications : Migration
{
    public override void Up()
    {
        Create.Table("AdminNotifications")
            .WithColumn("AdminNotificationId").AsGuid().NotNullable().PrimaryKey()
            // 種類（Core/Notifications/AdminNotificationKind）。
            // **`.Data` は `.Core` を参照しないので整数で持つ**（AttachmentRejections と同じ）
            .WithColumn("Kind").AsInt32().NotNullable()
            // **アンケートに紐づかない知らせでは Guid.Empty**
            .WithColumn("SurveyId").AsGuid().NotNullable()
            // **同じ知らせが何回起きたか。** 1 件ずつ行を作らないための数
            .WithColumn("Count").AsInt32().NotNullable()
            .WithColumn("FirstOccurredAt").AsDateTime2().NotNullable()
            .WithColumn("LastOccurredAt").AsDateTime2().NotNullable()
            // **既読は全体で 1 つ。** 誰かが既読にしたら全員にとって既読
            .WithColumn("ReadAt").AsDateTime2().Nullable();

        // **未読を数える／新しい順に読む。** どちらも画面を開くたびに走る
        Create.Index("IX_AdminNotifications_ReadAt_LastOccurredAt")
            .OnTable("AdminNotifications")
            .OnColumn("ReadAt").Ascending()
            .OnColumn("LastOccurredAt").Descending();

        // **未読の同じ種類を探すための索引。** 知らせを足すたびに引く
        Create.Index("IX_AdminNotifications_Kind_SurveyId")
            .OnTable("AdminNotifications")
            .OnColumn("Kind").Ascending()
            .OnColumn("SurveyId").Ascending();
    }

    public override void Down() => Delete.Table("AdminNotifications");
}
