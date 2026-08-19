using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>管理者ごとの表示言語。</summary>
/// <remarks>
/// <para>
/// **管理者は名前が分かっている相手なので、設定を人に紐づけてよい**
/// （<c>_documents/多言語対応方針.md</c> 2 章）。
/// 端末を変えても付いてくる方が、毎回 URL や合言葉と一緒に言語を選ぶより楽。
/// </para>
/// <para>
/// **回答者側には同じものを作らない。** 回答者は完全匿名で、
/// 言語の好みを保存すると回答者を絞り込む材料が 1 つ増える。
/// </para>
/// <para>
/// **NULL 可。** 「まだ選んでいない」を表す。
/// そのときはブラウザの言語設定に従い、最後は <c>ja</c> へ落ちる。
/// **既定値で <c>ja</c> を埋めない。** 埋めると「日本語を選んだ人」と
/// 「まだ選んでいない人」を区別できなくなる。
/// </para>
/// </remarks>
[Migration(5, "管理者ごとの表示言語")]
public sealed class M0005_AdminUserLanguage : Migration
{
    public override void Up() =>
        Alter.Table("AdminUsers")
            // **言語コードだけを入れる**（`ja` / `en`）。地域は持たない
            .AddColumn("Language").AsString(16).Nullable();

    public override void Down() => Delete.Column("Language").FromTable("AdminUsers");
}
