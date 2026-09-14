using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>回答の再編集リンクのトークン（Issue #202）。</summary>
/// <remarks>
/// <para>
/// **回答本体の <c>ResponseToken</c> をメールへ載せないための表。**
/// 外へ出すのはここのトークンだけで、**漏れても失効させれば回答本体は生き残る**
/// （<c>_documents/アーキテクチャ方針.md</c> 9 章の決まりの読み替え）。
/// </para>
/// <para>
/// ⚠️ **保存するのはハッシュだけ。** 表を読めた人が使えては意味が無い
/// （管理者の招待と同じ扱い）。
/// </para>
/// <para>
/// **期限内は何度でも使える**（2026-09-14 決定）。1 回で失効させると、
/// **メールのリンク検査（Defender 等）が先に開いた時点で本人が使えなくなる。**
/// </para>
/// </remarks>
[Migration(19, "回答の再編集リンクのトークン")]
public sealed class M0019_ResponseEditTokens : Migration
{
    public override void Up()
    {
        Create.Table("ResponseEditTokens")
            // **ハッシュそのものを主キーにする。** 引くのはハッシュからだけ
            .WithColumn("EditTokenHash").AsString(512).NotNullable().PrimaryKey()
            // **この回答を書き換えられる。** 回答本体のトークン
            .WithColumn("ResponseToken").AsString(64).NotNullable()
            // **一括失効はアンケート単位**（Issue #202 の決定 4）
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        // **アンケート単位の一括失効と、掃除で引く**
        Create.Index("IX_ResponseEditTokens_SurveyId")
            .OnTable("ResponseEditTokens")
            .OnColumn("SurveyId").Ascending();

        // **期限切れを掃除するときに引く**
        Create.Index("IX_ResponseEditTokens_ExpiresAt")
            .OnTable("ResponseEditTokens")
            .OnColumn("ExpiresAt").Ascending();
    }

    public override void Down() => Delete.Table("ResponseEditTokens");
}
