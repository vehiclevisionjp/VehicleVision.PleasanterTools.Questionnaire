using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests;

/// <summary>壊れた分岐を公開させないこと。</summary>
/// <remarks>
/// **ジャンプ方式は壊れた形を作れてしまう**（Issue #41）。
/// 無限に回る・辿り着けないページができる、といった不備は
/// **公開してからでは回答者にしか見えない**ので、公開の前に弾く。
/// </remarks>
public class SurveyFlowValidatorTests
{
    private static Question Radio(string id, params string[] choices) => new()
    {
        QuestionId = id,
        Type = QuestionType.Radio,
        Title = LocalizedText.Japanese(id),
        Choices = [.. choices.Select(value => new Choice(value, LocalizedText.Japanese(value)))],
    };

    private static Question Text(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese(id),
    };

    private static Page PageOf(string id, params Question[] questions) => new()
    {
        PageId = id,
        Questions = [.. questions],
    };

    private static SurveyDefinition Definition(params Page[] pages) => new()
    {
        SurveyId = Guid.NewGuid().ToString(),
        Version = 1,
        Title = LocalizedText.Japanese("見本"),
        Pages = [.. pages],
    };

    private static Question WithJump(Question question, string choiceValue, PageTransition next) =>
        question with
        {
            Choices = [.. question.Choices.Select(choice =>
                choice.Value == choiceValue ? choice with { Next = next } : choice)],
        };

    private static FlowProblemCode[] Codes(SurveyDefinition definition) =>
        [.. SurveyFlowValidator.Validate(definition).Select(problem => problem.Code)];

    // ---- 素直な定義 ---------------------------------------------------------

    [Fact]
    public void 壊れていなければ何も出ない()
    {
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "A", "B"), "B", PageTransition.To("p3"))),
            PageOf("p2", Text("q2")) with { Next = PageTransition.To("p4") },
            PageOf("p3", Text("q3")),
            PageOf("p4", Text("q4")));

        Assert.Empty(SurveyFlowValidator.Validate(definition));
    }

    // ---- 行き先 -------------------------------------------------------------

    [Fact]
    public void 存在しないページへの行き先を咎める()
    {
        var definition = Definition(
            PageOf("p1", Text("q1")) with { Next = PageTransition.To("どこか") },
            PageOf("p2", Text("q2")));

        Assert.Contains(FlowProblemCode.UnknownPage, Codes(definition));
    }

    [Fact]
    public void 前を向いた行き先を咎める()
    {
        // **許すと無限に回るアンケートが作れる**
        var definition = Definition(
            PageOf("p1", Text("q1")),
            PageOf("p2", Text("q2")) with { Next = PageTransition.To("p1") });

        Assert.Contains(FlowProblemCode.BackwardTransition, Codes(definition));
    }

    [Fact]
    public void 自分自身への行き先を咎める()
    {
        var definition = Definition(
            PageOf("p1", Text("q1")) with { Next = PageTransition.To("p1") },
            PageOf("p2", Text("q2")));

        Assert.Contains(FlowProblemCode.SelfTransition, Codes(definition));
    }

    [Fact]
    public void 一ページに行き先を持つ設問が二つあれば咎める()
    {
        // **どれが勝つのかを利用者が決められない**
        var definition = Definition(
            PageOf(
                "p1",
                WithJump(Radio("q1", "A", "B"), "A", PageTransition.To("p3")),
                WithJump(Radio("q2", "C", "D"), "C", PageTransition.To("p3"))),
            PageOf("p2", Text("q3")),
            PageOf("p3", Text("q4")));

        Assert.Contains(FlowProblemCode.MultipleBranchingQuestions, Codes(definition));
    }

    [Fact]
    public void 複数選択に行き先を付けたら咎める()
    {
        // **どの選択肢の行き先を使うのか決まらない**
        var checkbox = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Checkbox,
            Title = LocalizedText.Japanese("q1"),
            Choices =
            [
                new Choice("A", LocalizedText.Japanese("A"), Next: PageTransition.To("p3")),
                new Choice("B", LocalizedText.Japanese("B")),
            ],
        };

        var definition = Definition(
            PageOf("p1", checkbox),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        Assert.Contains(FlowProblemCode.TransitionOnUnsupportedQuestion, Codes(definition));
    }

    // ---- 到達できるか -------------------------------------------------------

    [Fact]
    public void 辿り着けないページを咎める()
    {
        // すべての選択肢が p3 へ飛ぶので、p2 へは決して行けない
        var branching = WithJump(
            WithJump(Radio("q1", "A", "B"), "A", PageTransition.To("p3")),
            "B",
            PageTransition.To("p3"));

        var definition = Definition(
            PageOf("p1", branching),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        Assert.Contains(FlowProblemCode.UnreachablePage, Codes(definition));
    }

    [Fact]
    public void 一部の選択肢だけ飛ぶなら辿り着ける()
    {
        // **行き先を持たない選択肢は次のページへ落ちる。** p2 へも行ける
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "A", "B"), "A", PageTransition.To("p3"))),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        Assert.DoesNotContain(FlowProblemCode.UnreachablePage, Codes(definition));
    }

    // ---- 表示条件 -----------------------------------------------------------

    [Fact]
    public void 存在しない設問を見る条件を咎める()
    {
        var definition = Definition(PageOf(
            "p1",
            Text("q1"),
            Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("居ない", ConditionOperator.Answered)],
                },
            }));

        Assert.Contains(FlowProblemCode.UnknownConditionQuestion, Codes(definition));
    }

    [Fact]
    public void 後ろを見る条件を咎める()
    {
        // **その時点でまだ答えていないので、成立しようがない**
        var definition = Definition(PageOf(
            "p1",
            Text("q1") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q2", ConditionOperator.Answered)],
                },
            },
            Text("q2")));

        Assert.Contains(FlowProblemCode.ForwardConditionReference, Codes(definition));
    }

    [Fact]
    public void 自分自身を見る条件を咎める()
    {
        var definition = Definition(PageOf(
            "p1",
            Text("q1") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.Answered)],
                },
            }));

        Assert.Contains(FlowProblemCode.ForwardConditionReference, Codes(definition));
    }

    [Fact]
    public void 無い選択肢を見る条件を咎める()
    {
        // **永久に成立しない。** 選択肢の値を変えたときの直し忘れが見つかる
        var definition = Definition(PageOf(
            "p1",
            Radio("q1", "はい", "いいえ"),
            Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.Equals, "たぶん")],
                },
            }));

        Assert.Contains(FlowProblemCode.UnknownChoiceValue, Codes(definition));
    }

    [Fact]
    public void 前のページの設問を見る条件は通す()
    {
        var definition = Definition(
            PageOf("p1", Radio("q1", "はい", "いいえ")),
            PageOf("p2", Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.Equals, "はい")],
                },
            }));

        Assert.Empty(SurveyFlowValidator.Validate(definition));
    }
}
