using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>SAML の設定を再起動せず変更できるようにする（Issue #254）。</summary>
[Migration(24, "SAML の設定を保存する")]
public sealed class M0024_SamlSettings : Migration
{
    public override void Up()
    {
        Create.Table("SamlSettings")
            .WithColumn("SamlSettingId").AsInt32().PrimaryKey()
            .WithColumn("Enabled").AsString(16).Nullable()
            .WithColumn("EntityId").AsString(2048).Nullable()
            .WithColumn("IdpEntityId").AsString(2048).Nullable()
            .WithColumn("SingleSignOnUrl").AsString(2048).Nullable()
            .WithColumn("IdpCertificate").AsString(int.MaxValue).Nullable()
            .WithColumn("UnknownUser").AsString(32).Nullable()
            .WithColumn("RegisterRole").AsString(64).Nullable()
            .WithColumn("LoginIdSource").AsString(32).Nullable()
            .WithColumn("LoginIdClaim").AsString(512).Nullable()
            .WithColumn("ButtonLabel").AsString(256).Nullable()
            .WithColumn("SingleLogoutUrl").AsString(2048).Nullable();

        Insert.IntoTable("SamlSettings")
            .Row(new { SamlSettingId = 1 });
    }

    public override void Down()
    {
        Delete.Table("SamlSettings");
    }
}
