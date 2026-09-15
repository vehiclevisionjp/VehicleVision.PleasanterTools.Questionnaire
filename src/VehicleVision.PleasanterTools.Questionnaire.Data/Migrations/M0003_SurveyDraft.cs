using FluentMigrator;

namespace VehicleVision.PleasanterTools.Questionnaire.Data.Migrations;

/// <summary>編集中のアンケート定義。</summary>
/// <remarks>
/// <para>
/// **公開済みのスナップショット（<c>SurveyVersions</c>）とは分ける**
/// （<c>_documents/データモデル設計.md</c> 1 章・2.2）。
/// 編集がそのまま回答画面へ出てしまうと、直している最中の設問に回答が付く。
/// </para>
/// <para>
/// **JSON 1 本ではなく表に分ける。** マッピングの入力が設問を指すため、
/// 設問を消したときに割り当てが宙に浮いていないかを表の側で見たい。
/// </para>
/// <para>
/// **多言語の器は最初から用意する**（<c>*Json</c> の列）。
/// 後から多言語化すると、全テーブルの文字列列を作り直すことになる。
/// </para>
/// </remarks>
[Migration(3, "編集中のアンケート定義")]
public sealed class M0003_SurveyDraft : Migration
{
    public override void Up()
    {
        // **同時に開いた 2 人が黙って上書きし合わないための版**
        Alter.Table("Surveys")
            .AddColumn("DraftRevision").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("DisplayMode").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("ShowProgress").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("AllowEditingAfterSubmit").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("DescriptionJson").AsString(int.MaxValue).Nullable()
            .AddColumn("ConfirmationMessageJson").AsString(int.MaxValue).Nullable()
            .AddColumn("TitleJson").AsString(int.MaxValue).Nullable();

        // **識別子はアンケートの中でだけ一意。** 全体で一意にすると、
        // アンケートを複製したときに必ず衝突する
        Create.Table("Pages")
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("PageId").AsString(64).NotNullable()
            .WithColumn("SortOrder").AsInt32().NotNullable()
            .WithColumn("TitleJson").AsString(int.MaxValue).Nullable()
            .WithColumn("DescriptionJson").AsString(int.MaxValue).Nullable();

        Create.PrimaryKey("PK_Pages").OnTable("Pages").Columns("SurveyId", "PageId");

        Create.Index("IX_Pages_SurveyId_SortOrder")
            .OnTable("Pages")
            .OnColumn("SurveyId").Ascending()
            .OnColumn("SortOrder").Ascending();

        Create.Table("Questions")
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("QuestionId").AsString(64).NotNullable()
            .WithColumn("PageId").AsString(64).NotNullable()
            .WithColumn("SortOrder").AsInt32().NotNullable()
            .WithColumn("QuestionType").AsInt32().NotNullable()
            .WithColumn("TitleJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("DescriptionJson").AsString(int.MaxValue).Nullable()
            .WithColumn("IsRequired").AsBoolean().NotNullable().WithDefaultValue(false)
            // 形式ごとの固有設定（尺度の上下限・検証規則・添付の上限など）
            .WithColumn("SettingsJson").AsString(int.MaxValue).Nullable();

        Create.PrimaryKey("PK_Questions").OnTable("Questions").Columns("SurveyId", "QuestionId");

        Create.Index("IX_Questions_SurveyId_PageId_SortOrder")
            .OnTable("Questions")
            .OnColumn("SurveyId").Ascending()
            .OnColumn("PageId").Ascending()
            .OnColumn("SortOrder").Ascending();

        Create.Table("QuestionChoices")
            .WithColumn("ChoiceId").AsGuid().NotNullable().PrimaryKey()
            // **設問と同じくアンケートで絞る。** 設問の識別子だけでは他のアンケートと混ざる
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("QuestionId").AsString(64).NotNullable()
            .WithColumn("SortOrder").AsInt32().NotNullable()
            // **Pleasanter の列へ写るのはこちら。** 画面に出る文字列は LabelJson
            .WithColumn("Value").AsString(512).NotNullable()
            .WithColumn("LabelJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("IsOther").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Index("IX_QuestionChoices_SurveyId_QuestionId_SortOrder")
            .OnTable("QuestionChoices")
            .OnColumn("SurveyId").Ascending()
            .OnColumn("QuestionId").Ascending()
            .OnColumn("SortOrder").Ascending();

        // ---- マッピング ----------------------------------------------------
        // **構造は Source(N) : Converter(0..1) : Target(1)**
        // 出力が 1 本に固定されているので、循環参照も合流の衝突も構造的に起こらない
        Create.Table("ColumnAssignments")
            .WithColumn("AssignmentId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("SurveyId").AsGuid().NotNullable()
            .WithColumn("TargetColumn").AsString(64).NotNullable()
            // **NULL なら入力は 1 つでなければならない**
            .WithColumn("ConverterOperation").AsString(64).Nullable()
            .WithColumn("ConverterConfigJson").AsString(int.MaxValue).Nullable()
            .WithColumn("SortOrder").AsInt32().NotNullable()
            // 画面用。実行には使わない
            .WithColumn("PositionX").AsInt32().Nullable()
            .WithColumn("PositionY").AsInt32().Nullable();

        // **同じ列への割り当てを 2 つ作らせない**
        Create.Index("UX_ColumnAssignments_SurveyId_TargetColumn")
            .OnTable("ColumnAssignments")
            .OnColumn("SurveyId").Ascending()
            .OnColumn("TargetColumn").Ascending()
            .WithOptions().Unique();

        Create.Table("AssignmentSources")
            .WithColumn("SourceId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("AssignmentId").AsGuid().NotNullable()
            .WithColumn("QuestionId").AsString(64).NotNullable()
            .WithColumn("Port").AsInt32().NotNullable()
            // **変換へ渡す順序。** 並びが変われば結果も変わる
            .WithColumn("SortOrder").AsInt32().NotNullable();

        Create.Index("IX_AssignmentSources_AssignmentId_SortOrder")
            .OnTable("AssignmentSources")
            .OnColumn("AssignmentId").Ascending()
            .OnColumn("SortOrder").Ascending();
    }

    public override void Down()
    {
        Delete.Table("AssignmentSources");
        Delete.Table("ColumnAssignments");
        Delete.Table("QuestionChoices");
        Delete.Table("Questions");
        Delete.Table("Pages");

        Delete.Column("TitleJson").FromTable("Surveys");
        Delete.Column("ConfirmationMessageJson").FromTable("Surveys");
        Delete.Column("DescriptionJson").FromTable("Surveys");
        Delete.Column("AllowEditingAfterSubmit").FromTable("Surveys");
        Delete.Column("ShowProgress").FromTable("Surveys");
        Delete.Column("DisplayMode").FromTable("Surveys");
        Delete.Column("DraftRevision").FromTable("Surveys");
    }
}
