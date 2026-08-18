using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者の締め出し状態を DB に持たせる。</summary>
/// <remarks>
/// <para>
/// **プロセス内の記憶では足りない。** Azure App Service は複数インスタンスで動きうるので、
/// 試行回数をメモリに置くと**インスタンスの数だけ試せる**ことになる。
/// </para>
/// <para>
/// 存在しないログイン ID への総当たりは、この表では止まらない。
/// そちらは**レート制限**が受け持つ（<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// </remarks>
[Migration(2, "管理者の締め出しと使い捨てパスワードの使い回し防止")]
public sealed class M0002_AdminLockout : Migration
{
    public override void Up()
    {
        Alter.Table("AdminUsers")
            .AddColumn("FailedLoginCount").AsInt32().NotNullable().WithDefaultValue(0)
            // **時刻で持つ。** 回数だけだと解除の判断ができない
            .AddColumn("LockedUntil").AsDateTime2().Nullable()
            // **通した時間枠を覚える。** 30 秒の間は同じ数字が通るので、
            // 盗み見られた数字がそのまま二度目に使えてしまう
            .AddColumn("TotpLastTimeStep").AsInt64().Nullable();
    }

    public override void Down()
    {
        Delete.Column("TotpLastTimeStep").FromTable("AdminUsers");
        Delete.Column("LockedUntil").FromTable("AdminUsers");
        Delete.Column("FailedLoginCount").FromTable("AdminUsers");
    }
}
