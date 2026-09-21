using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>システム全体のメンテナンス状態（Issue #360）。</summary>
[Migration(29, "システム全体のメンテナンス状態")]
public sealed class M0029_MaintenanceMode : Migration
{
    public override void Up()
    {
        Create.Table("MaintenanceMode")
            .WithColumn("MaintenanceModeId").AsInt32().NotNullable().PrimaryKey()
            .WithColumn("IsEnabled").AsBoolean().NotNullable()
            .WithColumn("MessageJa").AsString(500).Nullable()
            .WithColumn("MessageEn").AsString(500).Nullable()
            .WithColumn("EnabledAt").AsDateTime2().Nullable()
            .WithColumn("EnabledByAdminUserId").AsGuid().Nullable();

        Insert.IntoTable("MaintenanceMode").Row(new
        {
            MaintenanceModeId = 1,
            IsEnabled = false,
        });
    }

    public override void Down()
    {
        Delete.Table("MaintenanceMode");
    }
}
