using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>回答通知メールの管理者設定とアンケートごとの集約状態（Issue #357）。</summary>
/// <remarks>
/// <para>
/// **受け取りは管理者ごとのオプトイン。** 既存管理者も新しい管理者も既定は無効にする。
/// </para>
/// <para>
/// 画面通知の既読状態とは別の表で、アンケートごとに件数と時刻を集約する。
/// 既読後に画面通知が新しい行へ分かれても、メールは 24 時間に 1 通を超えない。
/// ⚠️ **回答本文・回答トークン・回答者の送信元は持たない。**
/// </para>
/// </remarks>
[Migration(28, "回答通知メールの管理者設定と集約状態")]
public sealed class M0028_ResponseNotifications : Migration
{
    public override void Up()
    {
        Alter.Table("AdminUsers")
            .AddColumn("ResponseNotificationEnabled")
            .AsBoolean()
            .NotNullable()
            .WithDefaultValue(false);

        Create.Table("ResponseNotificationDigests")
            .WithColumn("SurveyId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("Count").AsInt32().NotNullable()
            .WithColumn("MailQueuedCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("FirstOccurredAt").AsDateTime2().NotNullable()
            .WithColumn("LastOccurredAt").AsDateTime2().NotNullable()
            .WithColumn("LastMailQueuedAt").AsDateTime2().Nullable();

        Create.Index("IX_ResponseNotificationDigests_LastMailQueuedAt")
            .OnTable("ResponseNotificationDigests")
            .OnColumn("LastMailQueuedAt").Ascending();
    }

    public override void Down()
    {
        Delete.Table("ResponseNotificationDigests");
        Delete.Column("ResponseNotificationEnabled").FromTable("AdminUsers");
    }
}
