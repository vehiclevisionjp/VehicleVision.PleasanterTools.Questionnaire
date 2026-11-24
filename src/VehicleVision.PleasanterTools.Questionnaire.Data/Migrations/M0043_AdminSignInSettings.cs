using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理画面のログイン方式の設定を保存する表を作る（Issue #1056。親 Overview#60）。</summary>
/// <remarks>
/// <para>
/// <b>SAML の設定（<see cref="M0024_SamlSettings"/>）と同じ形の 1 行だけの表。</b>
/// 値は文字列で持ち、読むときに解釈する。外部設定（環境変数）と同じ解釈を通すため。
/// </para>
/// <para>
/// <b>移行の直後は <c>PasswordSignIn</c> が空（NULL）。</b>空は「指定なし」で、既定（ID/パスワードを隠さない）で動く。
/// 移行しただけで合言葉の入口が閉じることはない。
/// </para>
/// </remarks>
[Migration(43, "管理画面のログイン方式（ID/パスワードを隠すか）の設定を保存する")]
public sealed class M0043_AdminSignInSettings : Migration
{
    public override void Up()
    {
        Create.Table("AdminSignInSettings")
            .WithColumn("AdminSignInSettingId").AsInt32().PrimaryKey("PK_AdminSignInSettings")
            .WithColumn("PasswordSignIn").AsString(16).Nullable();

        Insert.IntoTable("AdminSignInSettings")
            .Row(new { AdminSignInSettingId = 1 });
    }

    public override void Down() => Delete.Table("AdminSignInSettings");
}
