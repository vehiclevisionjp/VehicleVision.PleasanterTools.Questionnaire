using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>Pleasanter のログインで管理画面へ入る設定を保存する（Issue #464）。</summary>
/// <remarks>
/// **SAML の設定（<see cref="M0024_SamlSettings"/>）と同じ形の 1 行だけの表。**
/// 値はすべて文字列で持ち、読むときに検証する。外部設定と同じ検証を通すため。
/// </remarks>
[Migration(32, "Pleasanter のログインで管理画面へ入る設定を保存する")]
public sealed class M0032_PleasanterSsoSettings : Migration
{
    public override void Up()
    {
        Create.Table("PleasanterSsoSettings")
            .WithColumn("PleasanterSsoSettingId").AsInt32().PrimaryKey()
            .WithColumn("Enabled").AsString(16).Nullable()
            .WithColumn("InternalBaseUrl").AsString(2048).Nullable()
            .WithColumn("LoginUrl").AsString(2048).Nullable()
            .WithColumn("LogoutUrl").AsString(2048).Nullable()
            .WithColumn("Method").AsString(32).Nullable()
            .WithColumn("SqlName").AsString(256).Nullable()
            .WithColumn("CookieNames").AsString(1024).Nullable()
            .WithColumn("UnknownUser").AsString(32).Nullable()
            .WithColumn("RegisterRole").AsString(64).Nullable()
            .WithColumn("RevalidateMinutes").AsString(16).Nullable()
            .WithColumn("TimeoutSeconds").AsString(16).Nullable()
            .WithColumn("ButtonLabel").AsString(256).Nullable();

        Insert.IntoTable("PleasanterSsoSettings")
            .Row(new { PleasanterSsoSettingId = 1 });
    }

    public override void Down()
    {
        Delete.Table("PleasanterSsoSettings");
    }
}
