using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理操作の記録に結果を持たせる。</summary>
/// <remarks>
/// <para>
/// **失敗した操作も残す**（Issue #19）。成功だけ残すと、試みられたことが分からない。
/// 「他人を止めようとして断られた」は、記録に残っていなければ起きなかったことになる。
/// </para>
/// <para>
/// **結果を <c>DetailJson</c> の中に埋めない。** 列にしておかないと
/// 「断られた操作だけ見る」が全件走査になる。
/// </para>
/// <para>
/// **NULL 可にしてある。** この列より前に書かれた行は結果を持たない。
/// 既定値で 200 を埋めると、成功したことになってしまう。
/// </para>
/// </remarks>
[Migration(6, "管理操作の記録に結果を持たせる")]
public sealed class M0006_AuditLogStatusCode : Migration
{
    public override void Up() =>
        Alter.Table("AuditLogs")
            .AddColumn("StatusCode").AsInt32().Nullable();

    public override void Down() => Delete.Column("StatusCode").FromTable("AuditLogs");
}
