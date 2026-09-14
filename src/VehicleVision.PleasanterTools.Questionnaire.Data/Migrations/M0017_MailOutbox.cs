using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>メールの送信待ちを溜める表（Issue #189）。</summary>
/// <remarks>
/// <para>
/// **回答の送信待ち（<c>Responses</c>）と同じ形にしてある。** 確保・指数バックオフ・
/// デッドレターの扱いを 2 通り覚えなくて済むようにするため。
/// </para>
/// <para>
/// ⚠️ **中身は暗号化して入れる**（<c>MailOutbox.PayloadProtected</c>）。
/// 宛先は回答者のメールアドレスで、本文には回答の写しが入り得る。
/// **完全匿名を前提にしたアプリで、個人を指す値を DB へ置く唯一の場所**なので、
/// 平文の列を作らない。**列を作らなければ、後から平文が入る余地も無い。**
/// </para>
/// <para>
/// **送れたら行を消す**（<c>Responses</c> と同じ。<c>Sent</c> の状態を持たない）。
/// 残り続けるのは、届いていない間だけ。
/// </para>
/// <para>
/// <c>SurveyId</c> は**アンケートに紐づかないメール（管理者の招待）では
/// <c>Guid.Empty</c>**（<c>AdminNotifications</c> と同じ理由。NULL 同士は等しくない）。
/// </para>
/// </remarks>
[Migration(17, "メールの送信待ちを溜める")]
public sealed class M0017_MailOutbox : Migration
{
    public override void Up()
    {
        Create.Table("MailOutbox")
            .WithColumn("MailId").AsGuid().NotNullable().PrimaryKey()
            // 種類（Core/Mail/MailKind）。**`.Data` は整数で持つ**（AdminNotifications と同じ）
            .WithColumn("Kind").AsInt32().NotNullable()
            // **紐づかないものは Guid.Empty**
            .WithColumn("SurveyId").AsGuid().NotNullable()
            // ⚠️ **宛先・件名・本文を 1 つにまとめて暗号化したもの。**
            // 列を分けると、暗号化の掛け忘れが列ごとに起き得る
            .WithColumn("PayloadProtected").AsString(int.MaxValue).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("RetryCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptAt").AsDateTime2().NotNullable()
            // **理由だけを入れる。宛先も本文も入れない**（画面とデッドレターに残る）
            .WithColumn("LastError").AsString(1024).Nullable()
            .WithColumn("LockedBy").AsString(64).Nullable()
            .WithColumn("LockedUntil").AsDateTime2().Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // 送信ワーカーが「送るべき行」を探す索引
        Create.Index("IX_MailOutbox_Status_NextAttemptAt")
            .OnTable("MailOutbox")
            .OnColumn("Status").Ascending()
            .OnColumn("NextAttemptAt").Ascending();
    }

    public override void Down() => Delete.Table("MailOutbox");
}
