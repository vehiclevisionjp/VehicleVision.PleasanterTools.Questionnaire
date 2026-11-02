using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>編集中のルックアップ定義の表を作る（Issue #820・仕様書 3.1 節）。</summary>
/// <remarks>
/// <para>
/// **非秘密の定義だけを持つ。** 資格情報（API キー）の暗号文は別の表（<c>LookupCredentials</c>）に置き、
/// ここには参照 ID（<c>CredentialId</c>）だけが JSON の中に入る。
/// </para>
/// <para>
/// **識別子はアンケートの中でだけ一意**（<c>Pages</c> と同じ）。複製のたびに衝突しない。
/// 公開時は <c>SurveyVersions.DefinitionJson</c> へ固定されるので、この表は下書き専用。
/// </para>
/// </remarks>
[Migration(39, "編集中のルックアップ定義の表を追加する")]
public sealed class M0039_SurveyLookups : Migration
{
    public override void Up()
    {
        Create.Table("SurveyLookups")
            .WithColumn("SurveyId").AsGuid().NotNullable().PrimaryKey("PK_SurveyLookups")
            .WithColumn("LookupId").AsString(64).NotNullable().PrimaryKey("PK_SurveyLookups")
            .WithColumn("SortOrder").AsInt32().NotNullable()
            .WithColumn("DefinitionJson").AsString(int.MaxValue).NotNullable();

        Create.Index("IX_SurveyLookups_SurveyId_SortOrder")
            .OnTable("SurveyLookups")
            .OnColumn("SurveyId").Ascending()
            .OnColumn("SortOrder").Ascending();
    }

    public override void Down() => Delete.Table("SurveyLookups");
}
