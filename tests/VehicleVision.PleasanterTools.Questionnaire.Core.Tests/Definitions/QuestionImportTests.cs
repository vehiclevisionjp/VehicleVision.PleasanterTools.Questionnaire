using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>設問の取り込み（Issue #358）。**DB を使わない。**</summary>
public class QuestionImportTests
{
    [Fact]
    public void 設問_ID_を取り込み先で使っていない値へ付け直す()
    {
        var ids = new Queue<string>(["q-existing", "q-new"]);

        var result = QuestionImport.Copy(
            [Question("q-source")],
            ["q-existing"],
            ids.Dequeue);

        Assert.Equal("q-new", Assert.Single(result.Questions).QuestionId);
    }

    [Fact]
    public void 選択肢の分岐と表示条件を除去する()
    {
        var source = Question("q-source") with
        {
            Choices =
            [
                new Choice(
                    "yes",
                    LocalizedText.Japanese("はい"),
                    Next: PageTransition.To("page-next")),
                new Choice("no", LocalizedText.Japanese("いいえ")),
            ],
            VisibleWhen = new VisibilityCondition
            {
                Rules = [new ConditionRule("q-before", ConditionOperator.Equals, "yes")],
            },
        };

        var result = QuestionImport.Copy([source], [], () => "q-new");
        var imported = Assert.Single(result.Questions);

        Assert.All(imported.Choices, choice => Assert.Null(choice.Next));
        Assert.Null(imported.VisibleWhen);
        Assert.Equal(1, result.RemovedChoiceTransitions);
        Assert.Equal(1, result.RemovedVisibilityConditions);
    }

    [Fact]
    public void 資産参照は表示文だけを残して除去する()
    {
        var source = Question("q-source") with
        {
            Type = QuestionType.Note,
            Description = new LocalizedText(new Dictionary<string, string>
            {
                ["ja"] =
                    "![案内図](asset:11111111-1111-4111-8111-111111111111)\n"
                    + "[資料](asset:22222222-2222-4222-8222-222222222222)",
                ["en"] = "No asset",
            }),
        };

        var result = QuestionImport.Copy([source], [], () => "q-new");
        var imported = Assert.Single(result.Questions);

        Assert.Equal("案内図\n資料", imported.Description!.Get("ja"));
        Assert.Equal("No asset", imported.Description.Get("en"));
        Assert.Equal(2, result.RemovedAssetReferences);
    }

    [Fact]
    public void プレーンな説明に書かれた_asset_文字列は変更しない()
    {
        const string text = "[資料](asset:11111111-1111-4111-8111-111111111111)";
        var source = Question("q-source") with
        {
            Description = LocalizedText.Japanese(text),
        };

        var result = QuestionImport.Copy([source], [], () => "q-new");

        Assert.Equal(text, Assert.Single(result.Questions).Description!.Get("ja"));
        Assert.Equal(0, result.RemovedAssetReferences);
    }

    [Fact]
    public void 設問の内容と元の設問は変えない()
    {
        var source = Question("q-source") with
        {
            IsRequired = true,
            Settings = new QuestionSettings { MaxLength = 20 },
            Choices =
            [
                new Choice(
                    "yes",
                    LocalizedText.Japanese("はい"),
                    Next: PageTransition.Submit),
            ],
        };

        var imported = Assert.Single(
            QuestionImport.Copy([source], [], () => "q-new").Questions);

        Assert.Equal(QuestionType.Text, imported.Type);
        Assert.Equal("設問", imported.Title.Get("ja"));
        Assert.True(imported.IsRequired);
        Assert.Equal(20, imported.Settings.MaxLength);
        Assert.NotNull(source.Choices[0].Next);
        Assert.Equal("q-source", source.QuestionId);
    }

    [Fact]
    public void 複数の設問へ互いに異なる_ID_を付ける()
    {
        var ids = new Queue<string>(["q-new-1", "q-new-2"]);

        var result = QuestionImport.Copy(
            [Question("q-source-1"), Question("q-source-2")],
            [],
            ids.Dequeue);

        Assert.Equal(["q-new-1", "q-new-2"], result.Questions.Select(q => q.QuestionId));
    }

    private static Question Question(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese("設問"),
        IsRequired = false,
        Choices = ImmutableArray<Choice>.Empty,
    };
}
