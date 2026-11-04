using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>
/// 管理者通知の行へ、ルックアップの検索 ID と直近の相関 ID の列を足す（Issue #836・仕様書 4.3.1 節）。
/// </summary>
/// <remarks>
/// <para>
/// **どちらも NULL 可。** ルックアップ以外の通知（回答・滞留など）では使わず、既存の行も NULL のまま。
/// **集約の単位（種類・アンケート・未読）は変えない**ので、既存の通知の件数は変わらない。
/// 同じ行へ複数回起きたときは、**直近のもの**で上書きする。
/// </para>
/// <para>
/// **秘密を持たせない。** 検索 ID は定義の識別子、相関 ID は要求の識別子（<c>TraceIdentifier</c>）だけ。
/// キー値・取得値・資格情報・値のハッシュを入れる列は作らない。
/// </para>
/// </remarks>
[Migration(41, "管理者通知へ検索 ID と直近の相関 ID の列を追加する")]
public sealed class M0041_AdminNotificationLookupIds : Migration
{
    public override void Up()
    {
        Alter.Table("AdminNotifications")
            .AddColumn("LookupId").AsString(64).Nullable();

        Alter.Table("AdminNotifications")
            .AddColumn("LastCorrelationId").AsString(64).Nullable();
    }

    public override void Down()
    {
        Delete.Column("LastCorrelationId").FromTable("AdminNotifications");
        Delete.Column("LookupId").FromTable("AdminNotifications");
    }
}
