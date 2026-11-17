using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>ルックアップの専用資格情報の表を作る（Issue #821・仕様書 3.1・3.3 節）。</summary>
/// <remarks>
/// <para>
/// **サーバー専用。** 暗号文（<c>SecretCipher</c>）は管理画面にも回答画面にも返さない。
/// 暗号化は <c>SecretProtector</c>（AES-GCM）で、文脈をアンケート ID・資格情報 ID に束縛する。
/// </para>
/// <para>
/// **公開済みの版には資格情報を入れない。** 定義には参照 ID（<c>CredentialId</c>）だけが入り、
/// 停止・失効（<c>IsEnabled</c>）は公開し直さずに実行時へ効く。
/// </para>
/// </remarks>
[Migration(40, "ルックアップの専用資格情報の表を追加する")]
public sealed class M0040_LookupCredentials : Migration
{
    public override void Up()
    {
        Create.Table("LookupCredentials")
            .WithColumn("CredentialId").AsString(64).NotNullable().PrimaryKey("PK_LookupCredentials")
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("SourceKind").AsInt32().NotNullable()
            .WithColumn("SecretCipher").AsString(int.MaxValue).NotNullable()
            .WithColumn("IsEnabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("UpdatedBy").AsString(128).Nullable()
            .WithColumn("UpdatedAt").AsDateTime().NotNullable();

        Create.Index("IX_LookupCredentials_SurveyId")
            .OnTable("LookupCredentials")
            .OnColumn("SurveyId").Ascending();
    }

    public override void Down() => Delete.Table("LookupCredentials");
}
