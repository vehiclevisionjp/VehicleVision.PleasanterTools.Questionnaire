using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests;

/// <summary>回答に応じて辿るページと出す設問が決まること。</summary>
/// <remarks>
/// **ジャンプ方式**（Issue #41）。**サーバ側で必ず通す。**
/// 画面が辿った経路を信じると、画面を通さずに送るだけで隠した設問へ書き込める。
/// </remarks>
public class SurveyFlowTests
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

    /// <summary>選択肢に行き先を付ける。</summary>
    private static Question WithJump(Question question, string choiceValue, PageTransition next) =>
        question with
        {
            Choices = [.. question.Choices.Select(choice =>
                choice.Value == choiceValue ? choice with { Next = next } : choice)],
        };

    // ---- ページの分岐 -------------------------------------------------------

    [Fact]
    public void 何も指定しなければ順に辿る()
    {
        var definition = Definition(
            PageOf("p1", Text("q1")),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        var path = SurveyFlow.Trace(definition, [Answer.Of("q1", "あ")]);

        Assert.Equal(["p1", "p2", "p3"], path.Pages.Select(page => page.PageId));
    }

    [Fact]
    public void 選んだ選択肢の行き先へ飛ぶ()
    {
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "満足", "不満"), "満足", PageTransition.To("p3"))),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        var satisfied = SurveyFlow.Trace(definition, [Answer.Of("q1", "満足")]);
        Assert.Equal(["p1", "p3"], satisfied.Pages.Select(page => page.PageId));

        // **行き先を持たない選択肢は次のページへ落ちる**
        var unsatisfied = SurveyFlow.Trace(definition, [Answer.Of("q1", "不満")]);
        Assert.Equal(["p1", "p2", "p3"], unsatisfied.Pages.Select(page => page.PageId));
    }

    [Fact]
    public void ページ末尾の行き先で合流できる()
    {
        // **分かれた道を戻す。** ジャンプ方式ではこれが無いと最後まで分かれたままになる
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "A", "B"), "B", PageTransition.To("p3"))),
            PageOf("p2", Text("q2")) with { Next = PageTransition.To("p4") },
            PageOf("p3", Text("q3")),
            PageOf("p4", Text("q4")));

        var viaTwo = SurveyFlow.Trace(definition, [Answer.Of("q1", "A")]);
        Assert.Equal(["p1", "p2", "p4"], viaTwo.Pages.Select(page => page.PageId));

        var viaThree = SurveyFlow.Trace(definition, [Answer.Of("q1", "B")]);
        Assert.Equal(["p1", "p3", "p4"], viaThree.Pages.Select(page => page.PageId));
    }

    [Fact]
    public void 送信を指す行き先でそこで終わる()
    {
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "はい", "いいえ"), "いいえ", PageTransition.Submit)),
            PageOf("p2", Text("q2")));

        var path = SurveyFlow.Trace(definition, [Answer.Of("q1", "いいえ")]);

        Assert.Equal(["p1"], path.Pages.Select(page => page.PageId));
    }

    [Fact]
    public void 未回答なら飛ばずに次のページへ()
    {
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "満足", "不満"), "満足", PageTransition.To("p3"))),
            PageOf("p2", Text("q2")),
            PageOf("p3", Text("q3")));

        var path = SurveyFlow.Trace(definition, []);

        Assert.Equal(["p1", "p2", "p3"], path.Pages.Select(page => page.PageId));
    }

    [Fact]
    public void 同じページを二度通らない()
    {
        // **検査を抜けた定義でも止まる。** 前を向いた行き先は公開時に弾くが、
        // 評価する側も無限に回らないようにしておく
        var definition = Definition(
            PageOf("p1", Text("q1")) with { Next = PageTransition.To("p2") },
            PageOf("p2", Text("q2")) with { Next = PageTransition.To("p1") });

        var path = SurveyFlow.Trace(definition, []);

        Assert.Equal(["p1", "p2"], path.Pages.Select(page => page.PageId));
    }

    // ---- 設問の出し分け -----------------------------------------------------

    [Fact]
    public void 条件を満たす設問だけ出す()
    {
        var definition = Definition(PageOf(
            "p1",
            Radio("q1", "はい", "いいえ"),
            Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.Equals, "はい")],
                },
            }));

        Assert.True(SurveyFlow.Trace(definition, [Answer.Of("q1", "はい")]).Visible("q2"));
        Assert.False(SurveyFlow.Trace(definition, [Answer.Of("q1", "いいえ")]).Visible("q2"));
        Assert.False(SurveyFlow.Trace(definition, []).Visible("q2"));
    }

    [Fact]
    public void 飛ばされたページの設問は未回答として扱う()
    {
        // **答えが残っていても、見せていない以上は無かったことにする。**
        // 残った答えで条件が成立すると、経路と画面が食い違う
        var definition = Definition(
            PageOf("p1", WithJump(Radio("q1", "飛ばす", "通る"), "飛ばす", PageTransition.To("p3"))),
            PageOf("p2", Radio("q2", "あ", "い")),
            PageOf("p3", Text("q3") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q2", ConditionOperator.Answered)],
                },
            }));

        // p2 を飛ばしたのに q2 の答えが送られてきた場合
        var path = SurveyFlow.Trace(
            definition, [Answer.Of("q1", "飛ばす"), Answer.Of("q2", "あ")]);

        Assert.False(path.Visited("p2"));
        Assert.False(path.Visible("q2"));
        Assert.False(path.Visible("q3"));
    }

    [Fact]
    public void すべて満たすときとどれか満たすときを選べる()
    {
        Question conditional(ConditionMatch match) => Text("q3") with
        {
            VisibleWhen = new VisibilityCondition
            {
                Match = match,
                Rules =
                [
                    new ConditionRule("q1", ConditionOperator.Equals, "はい"),
                    new ConditionRule("q2", ConditionOperator.Equals, "はい"),
                ],
            },
        };

        var answers = new[] { Answer.Of("q1", "はい"), Answer.Of("q2", "いいえ") };

        var all = Definition(PageOf(
            "p1", Radio("q1", "はい", "いいえ"), Radio("q2", "はい", "いいえ"),
            conditional(ConditionMatch.All)));
        Assert.False(SurveyFlow.Trace(all, answers).Visible("q3"));

        var any = Definition(PageOf(
            "p1", Radio("q1", "はい", "いいえ"), Radio("q2", "はい", "いいえ"),
            conditional(ConditionMatch.Any)));
        Assert.True(SurveyFlow.Trace(any, answers).Visible("q3"));
    }

    [Fact]
    public void 未回答は等しくないに含めない()
    {
        // **含めると、まだ答えていないだけの設問で条件が成立してしまう**
        var definition = Definition(PageOf(
            "p1",
            Radio("q1", "はい", "いいえ"),
            Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.NotEquals, "はい")],
                },
            }));

        Assert.False(SurveyFlow.Trace(definition, []).Visible("q2"));
        Assert.True(SurveyFlow.Trace(definition, [Answer.Of("q1", "いいえ")]).Visible("q2"));
    }

    [Fact]
    public void 数の大小で出し分けられる()
    {
        var definition = Definition(PageOf(
            "p1",
            new Question
            {
                QuestionId = "q1",
                Type = QuestionType.Scale,
                Title = LocalizedText.Japanese("満足度"),
            },
            Text("q2") with
            {
                VisibleWhen = new VisibilityCondition
                {
                    Rules = [new ConditionRule("q1", ConditionOperator.LessThan, "3")],
                },
            }));

        Assert.True(SurveyFlow.Trace(definition, [Answer.Of("q1", "2")]).Visible("q2"));
        Assert.False(SurveyFlow.Trace(definition, [Answer.Of("q1", "4")]).Visible("q2"));

        // **数として読めなければ成立しない**
        Assert.False(SurveyFlow.Trace(definition, [Answer.Of("q1", "ふつう")]).Visible("q2"));
    }

    [Fact]
    public void 出していない設問の選択肢では飛ばさない()
    {
        // **隠れている設問の答えでページが飛ぶと、画面と経路が食い違う**
        var definition = Definition(
            PageOf(
                "p1",
                Radio("q1", "はい", "いいえ"),
                WithJump(Radio("q2", "A", "B"), "A", PageTransition.To("p3")) with
                {
                    VisibleWhen = new VisibilityCondition
                    {
                        Rules = [new ConditionRule("q1", ConditionOperator.Equals, "はい")],
                    },
                }),
            PageOf("p2", Text("q3")),
            PageOf("p3", Text("q4")));

        var path = SurveyFlow.Trace(
            definition, [Answer.Of("q1", "いいえ"), Answer.Of("q2", "A")]);

        Assert.False(path.Visible("q2"));
        Assert.Equal(["p1", "p2", "p3"], path.Pages.Select(page => page.PageId));
    }
}
