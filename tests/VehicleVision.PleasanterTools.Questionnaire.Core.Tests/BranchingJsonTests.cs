using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests;

/// <summary>分岐の設定が JSON を往復しても失われないこと。</summary>
/// <remarks>
/// **公開時のスナップショットは JSON で入る**（<c>_documents/データモデル設計.md</c> 2.1）。
/// **往復できないと、公開した瞬間に分岐が消える。**
/// </remarks>
public class BranchingJsonTests
{
    private static T RoundTrip<T>(T value)
        where T : class =>
        SurveyJson.Deserialize<T>(SurveyJson.Serialize(value))!;

    [Fact]
    public void 行き先が往復する()
    {
        var page = RoundTrip(PageTransition.To("p3"));

        Assert.Equal(PageTransitionKind.Page, page.Kind);
        Assert.Equal("p3", page.PageId);

        Assert.Equal(PageTransitionKind.Submit, RoundTrip(PageTransition.Submit).Kind);
        Assert.Equal(PageTransitionKind.Next, RoundTrip(PageTransition.Next).Kind);
    }

    [Fact]
    public void 表示条件が往復する()
    {
        var condition = RoundTrip(new VisibilityCondition
        {
            Match = ConditionMatch.Any,
            Rules =
            [
                new ConditionRule("q1", ConditionOperator.Equals, "はい"),
                new ConditionRule("q2", ConditionOperator.NotAnswered),
            ],
        });

        Assert.Equal(ConditionMatch.Any, condition.Match);
        Assert.Equal(2, condition.Rules.Length);
        Assert.Equal("はい", condition.Rules[0].Value);
        Assert.Equal(ConditionOperator.NotAnswered, condition.Rules[1].Operator);
        Assert.Null(condition.Rules[1].Value);
    }

    [Fact]
    public void 列挙は文字列で書く()
    {
        // **数値だと、列挙に値を挿入したときに過去の版の意味が変わる**
        var json = SurveyJson.Serialize(PageTransition.Submit);

        Assert.Contains("Submit", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 分岐を持つ定義がまるごと往復する()
    {
        var definition = new SurveyDefinition
        {
            SurveyId = Guid.NewGuid().ToString(),
            Version = 3,
            Title = LocalizedText.Japanese("見本"),
            Pages =
            [
                new Page
                {
                    PageId = "p1",
                    Next = PageTransition.To("p3"),
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "q1",
                            Type = QuestionType.Radio,
                            Title = LocalizedText.Japanese("満足度"),
                            Choices =
                            [
                                new Choice(
                                    "満足",
                                    LocalizedText.Japanese("満足"),
                                    Next: PageTransition.Submit),
                                new Choice("不満", LocalizedText.Japanese("不満")),
                            ],
                        },
                        new Question
                        {
                            QuestionId = "q2",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("理由"),
                            VisibleWhen = new VisibilityCondition
                            {
                                Rules = [new ConditionRule("q1", ConditionOperator.Equals, "不満")],
                            },
                        },
                    ],
                },
                new Page { PageId = "p3", Questions = [] },
            ],
        };

        var restored = RoundTrip(definition);

        Assert.Equal("p3", restored.Pages[0].Next!.PageId);
        Assert.Equal(
            PageTransitionKind.Submit, restored.Pages[0].Questions[0].Choices[0].Next!.Kind);
        Assert.Null(restored.Pages[0].Questions[0].Choices[1].Next);
        Assert.Equal(
            "不満", restored.Pages[0].Questions[1].VisibleWhen!.Rules[0].Value);
    }

    [Fact]
    public void 分岐の無い定義は今までどおり読める()
    {
        // **公開済みの版に移行は要らない。** 読むときに既定値が入るだけ
        const string json = """
            {
              "surveyId": "s1",
              "version": 1,
              "title": { "ja": "見本" },
              "pages": [
                {
                  "pageId": "p1",
                  "questions": [
                    { "questionId": "q1", "type": "Text", "title": { "ja": "名前" } }
                  ]
                }
              ]
            }
            """;

        var definition = SurveyJson.Deserialize<SurveyDefinition>(json)!;

        Assert.Null(definition.Pages[0].Next);
        Assert.Null(definition.Pages[0].Questions[0].VisibleWhen);
    }
}
