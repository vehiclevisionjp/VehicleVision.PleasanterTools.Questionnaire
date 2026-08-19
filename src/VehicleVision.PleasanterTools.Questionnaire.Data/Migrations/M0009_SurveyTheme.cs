using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>回答画面のテーマ（色・書体）とヘッダ画像。</summary>
/// <remarks>
/// <para>
/// **テーマは JSON 1 本で持つ**（Issue #56）。色を 1 本足すたびに移行が要る形にしない
/// （<c>SettingsJson</c> や <c>NextJson</c> と同じ考え。<c>M0007_Branching</c>）。
/// <c>_documents/データモデル設計.md</c> 2.1 が挙げている <c>Theme</c> 列にあたる。
/// </para>
/// <para>
/// **画像は別の表に置く。** 定義の JSON へ入れると、公開のたびに
/// <c>SurveyVersions</c> の各版へ丸ごと複製され、
/// **回答画面が定義を読むだけで画像を受け取る**ことになる。
/// </para>
/// <para>
/// **画像は Base64 の文字列で持つ。** 3 者の binary 型は
/// <c>varbinary(max)</c> / <c>bytea</c> / <c>longblob</c> と実体も既定の扱いも違い、
/// **長い文字列だけが 3 者で実測済み**（<c>_documents/データモデル設計.md</c> 4 章）。
/// 添付が <c>PayloadJson</c> へ Base64 で載っているのと同じやり方に揃える。
/// **元のバイト数の約 4/3 になる**ので、容量を見積もるときはその分を足すこと。
/// </para>
/// <para>
/// **公開済みの版には移行が要らない。** あちらは定義まるごとの JSON なので、
/// 読むときに <c>Theme</c> が <c>null</c>（＝既定の見た目）になるだけ。
/// </para>
/// </remarks>
[Migration(9, "回答画面のテーマとヘッダ画像")]
public sealed class M0009_SurveyTheme : Migration
{
    public override void Up()
    {
        // **NULL は「既定の見た目」。** 触っていないアンケートは今までのまま
        Alter.Table("Surveys")
            .AddColumn("ThemeJson").AsString(int.MaxValue).Nullable();

        // **行は上書きしない。** 差し替えは新しい行を足して、
        // テーマが指す識別子を差し替えることで表す。
        // こうしておくと、**公開済みの版が指している画像は差し替えても変わらない**
        Create.Table("SurveyAssets")
            .WithColumn("AssetId").AsGuid().NotNullable().PrimaryKey()
            // **どのアンケートのものか。** 読み出しは必ずこれで絞る
            // （識別子だけで引けると、他のアンケートの画像を取り出せる）
            .WithColumn("SurveyId").AsGuid().NotNullable()
            // **配信するときの型。** 上げた人が名乗った型ではなく、
            // 検査を通った拡張子からサーバが決めた値だけを入れる
            .WithColumn("ContentType").AsString(64).NotNullable()
            // 管理画面に出す用。**配信の判断には使わない**
            .WithColumn("FileName").AsString(256).NotNullable()
            .WithColumn("ByteSize").AsInt64().NotNullable()
            // **Base64。** 3 者で実測済みの長い文字列の型へ写る
            .WithColumn("ContentBase64").AsString(int.MaxValue).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        // アンケートを消すときに、ぶら下がっている画像を見つけるための索引
        Create.Index("IX_SurveyAssets_SurveyId")
            .OnTable("SurveyAssets").OnColumn("SurveyId").Ascending();
    }

    public override void Down()
    {
        Delete.Table("SurveyAssets");
        Delete.Column("ThemeJson").FromTable("Surveys");
    }
}
