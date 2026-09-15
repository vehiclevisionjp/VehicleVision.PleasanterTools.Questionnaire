using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>必須が、通った経路の見えている設問にだけ効くこと。</summary>
/// <remarks>
/// **通らなかったページの必須を求めると、答えようのない設問で弾くことになる**（Issue #41）。
/// </remarks>
public class BranchingValidationTests
{
    private static Question Radio(string id, params string[] choices) => new()
    {
        QuestionId = id,
        Type = QuestionType.Radio,
        Title = LocalizedText.Japanese(id),
        Choices = [.. choices.Select(value => new Choice(value, LocalizedText.Japanese(value)))],
    };

    private static Question RequiredText(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese(id),
        IsRequired = true,
    };

    private static Question WithJump(Question question, string choiceValue, PageTransition next) =>
        question with
        {
            Choices = [.. question.Choices.Select(choice =>
                choice.Value == choiceValue ? choice with { Next = next } : choice)],
        };

    private static SurveyDefinition Definition(params Page[] pages) => new()
    {
        SurveyId = Guid.NewGuid().ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("見本"),
        Pages = [.. pages],
    };

    [Fact]
    public void 飛ばしたページの必須は求めない()
    {
        var definition = Definition(
            new Page
            {
                PageId = "p1",
                Questions = [WithJump(Radio("q1", "飛ばす", "通る"), "飛ばす", PageTransition.To("p3"))],
            },
            new Page { PageId = "p2", Questions = [RequiredText("q2")] },
            new Page { PageId = "p3", Questions = [] });

        // 飛ばした側は q2 に答えなくても通る
        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "飛ばす")]));

        // 通った側は q2 が要る
        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "通る")]);
        Assert.Contains(errors, error => error.QuestionId == "q2");
    }

    [Fact]
    public void 出していない設問の必須は求めない()
    {
        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions =
            [
                Radio("q1", "はい", "いいえ"),
                RequiredText("q2") with
                {
                    VisibleWhen = new VisibilityCondition
                    {
                        Rules = [new ConditionRule("q1", ConditionOperator.Equals, "はい")],
                    },
                },
            ],
        });

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "いいえ")]));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "はい")]);
        Assert.Contains(errors, error => error.QuestionId == "q2");
    }

    [Fact]
    public void 定義に無い設問への回答は今までどおり咎める()
    {
        // **分岐で隠れているのと、そもそも存在しないのは別**
        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions = [Radio("q1", "はい", "いいえ")],
        });

        var errors = AnswerValidator.Validate(definition, [Answer.Of("居ない", "何か")]);

        Assert.Contains(errors, error => error.Code == ValidationErrorCode.UnknownQuestion);
    }

    [Fact]
    public void 隠れている設問へ答えても咎めない()
    {
        // **咎めない。** 答えを変えた直後など、画面が消し切る前に届くことがある。
        // 落とすのは受け付ける側の仕事（ResponseIntake）
        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions =
            [
                Radio("q1", "はい", "いいえ"),
                new Question
                {
                    QuestionId = "q2",
                    Type = QuestionType.Text,
                    Title = LocalizedText.Japanese("q2"),
                    VisibleWhen = new VisibilityCondition
                    {
                        Rules = [new ConditionRule("q1", ConditionOperator.Equals, "はい")],
                    },
                },
            ],
        });

        Assert.Empty(AnswerValidator.Validate(
            definition, [Answer.Of("q1", "いいえ"), Answer.Of("q2", "余分な答え")]));
    }

    [Fact]
    public void ページ単位の検証も通った経路だけ見る()
    {
        var definition = Definition(
            new Page
            {
                PageId = "p1",
                Questions = [WithJump(Radio("q1", "飛ばす", "通る"), "飛ばす", PageTransition.To("p3"))],
            },
            new Page { PageId = "p2", Questions = [RequiredText("q2")] },
            new Page { PageId = "p3", Questions = [] });

        // **通っていないページを名指ししても、何も出ない**
        Assert.Empty(AnswerValidator.ValidatePage(
            definition, "p2", [Answer.Of("q1", "飛ばす")]));

        Assert.NotEmpty(AnswerValidator.ValidatePage(
            definition, "p2", [Answer.Of("q1", "通る")]));
    }
}
