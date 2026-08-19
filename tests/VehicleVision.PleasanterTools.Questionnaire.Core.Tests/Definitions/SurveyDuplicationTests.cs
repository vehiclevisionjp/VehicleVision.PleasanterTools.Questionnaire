using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>アンケートの複製（Issue #46）。**DB を使わない。**</summary>
public class SurveyDuplicationTests
{
    private const string NewSurveyId = "22222222-2222-2222-2222-222222222222";

    /// <summary>分岐を持った定義。**写し漏れを見つけるための材料。**</summary>
    private static SurveyDefinition CreateDefinition() => new()
    {
        SurveyId = "11111111-1111-1111-1111-111111111111",
        Version = 4,
        Title = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "満足度調査",
            ["en"] = "Satisfaction survey",
        }),
        Description = LocalizedText.Japanese("ご協力ください"),
        ConfirmationMessage = LocalizedText.Japanese("ありがとうございました"),
        DisplayMode = DisplayMode.OneQuestionPerPage,
        ShowProgress = false,
        AllowEditingAfterSubmit = false,
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Title = LocalizedText.Japanese("1 ページ目"),
                // ページ末尾の行き先
                Next = PageTransition.To("p3"),
                Questions =
                [
                    new Question
                    {
                        QuestionId = "q1",
                        Type = QuestionType.Radio,
                        Title = LocalizedText.Japanese("ご満足いただけましたか"),
                        IsRequired = true,
                        Choices =
                        [
                            // 選択肢ごとの行き先
                            new Choice("yes", LocalizedText.Japanese("はい"), Next: PageTransition.Submit),
                            new Choice("no", LocalizedText.Japanese("いいえ"), Next: PageTransition.To("p2")),
                        ],
                    },
                ],
            },
            new Page
            {
                PageId = "p2",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "q2",
                        Type = QuestionType.Paragraph,
                        Title = LocalizedText.Japanese("差し支えなければ理由をお聞かせください"),
                        // 設問の出し分け
                        VisibleWhen = new VisibilityCondition
                        {
                            Match = ConditionMatch.Any,
                            Rules = [new ConditionRule("q1", ConditionOperator.Equals, "no")],
                        },
                    },
                ],
            },
            new Page { PageId = "p3" },
        ],
    };

    [Fact]
    public void 内部_ID_が入れ替わる()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        Assert.Equal(NewSurveyId, copy.SurveyId);
    }

    /// <summary>**公開済みの版は写さない**ので、次に公開されるのは 1 版目。</summary>
    [Fact]
    public void 版が_1_に戻る()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        Assert.Equal(1, copy.Version);
    }

    /// <summary>**題名の言語に合わせて付ける。** 操作した人の言語では選ばない。</summary>
    [Fact]
    public void 題名が言語ごとにコピーになる()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        Assert.Equal("満足度調査のコピー", copy.Title.Get("ja"));
        Assert.Equal("Satisfaction survey (copy)", copy.Title.Get("en"));
    }

    /// <summary>知らない言語でも、元の題名だけが残らないようにする。</summary>
    [Fact]
    public void 対応していない言語にも既定の接尾辞が付く()
    {
        var title = new LocalizedText(new Dictionary<string, string> { ["fr"] = "Enquête" });

        var copied = SurveyDuplication.CopyTitle(title);

        Assert.Equal("Enquêteのコピー", copied.Get("fr"));
    }

    /// <summary>**題名以外の文言は変えない。** 説明まで「のコピー」が付くと回答者に見える。</summary>
    [Fact]
    public void 説明と送信後の文言はそのまま写る()
    {
        var source = CreateDefinition();

        var copy = SurveyDuplication.Copy(source, NewSurveyId);

        Assert.Equal("ご協力ください", copy.Description?.Get("ja"));
        Assert.Equal("ありがとうございました", copy.ConfirmationMessage?.Get("ja"));
    }

    [Fact]
    public void 表示の設定が写る()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        Assert.Equal(DisplayMode.OneQuestionPerPage, copy.DisplayMode);
        Assert.False(copy.ShowProgress);
        Assert.False(copy.AllowEditingAfterSubmit);
    }

    /// <summary>**分岐は最近入った項目なので写し漏らしやすい**（Issue #41 → #46）。</summary>
    [Fact]
    public void ページ末尾の行き先が写る()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        var page = copy.Pages.Single(page => page.PageId == "p1");
        Assert.Equal(PageTransitionKind.Page, page.Next?.Kind);
        Assert.Equal("p3", page.Next?.PageId);
    }

    [Fact]
    public void 選択肢ごとの行き先が写る()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        var choices = copy.FindQuestion("q1")!.Choices;
        Assert.Equal(PageTransitionKind.Submit, choices[0].Next?.Kind);
        Assert.Equal(PageTransitionKind.Page, choices[1].Next?.Kind);
        Assert.Equal("p2", choices[1].Next?.PageId);
    }

    [Fact]
    public void 設問の表示条件が写る()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        var condition = copy.FindQuestion("q2")!.VisibleWhen;
        Assert.NotNull(condition);
        Assert.Equal(ConditionMatch.Any, condition.Match);
        var rule = Assert.Single(condition.Rules);
        Assert.Equal("q1", rule.QuestionId);
        Assert.Equal(ConditionOperator.Equals, rule.Operator);
        Assert.Equal("no", rule.Value);
    }

    /// <summary>
    /// **識別子は振り直さない。** 分岐もマッピングもこの識別子を指しており、
    /// 振り直すと参照先を全部書き換えることになる。
    /// **アンケートの中でだけ一意**なので、写しても衝突しない。
    /// </summary>
    [Fact]
    public void ページと設問の識別子は振り直さない()
    {
        var copy = SurveyDuplication.Copy(CreateDefinition(), NewSurveyId);

        Assert.Equal(["p1", "p2", "p3"], copy.Pages.Select(page => page.PageId));
        Assert.Equal(["q1", "q2"], copy.AllQuestions.Select(question => question.QuestionId));
    }

    /// <summary>**元は変えない。** 複製したら元の題名まで変わっては困る。</summary>
    [Fact]
    public void 元の定義を変えない()
    {
        var source = CreateDefinition();

        _ = SurveyDuplication.Copy(source, NewSurveyId);

        Assert.Equal("満足度調査", source.Title.Get("ja"));
        Assert.Equal(4, source.Version);
        Assert.Equal("11111111-1111-1111-1111-111111111111", source.SurveyId);
    }

    /// <summary>ページを 1 つも持たない下書きでも複製できる。</summary>
    [Fact]
    public void 空の定義でも複製できる()
    {
        var source = new SurveyDefinition
        {
            SurveyId = "s0",
            Version = 1,
            Title = LocalizedText.Japanese("未設定"),
            Pages = ImmutableArray<Page>.Empty,
        };

        var copy = SurveyDuplication.Copy(source, NewSurveyId);

        Assert.Empty(copy.Pages);
        Assert.Equal("未設定のコピー", copy.Title.Get("ja"));
    }

    [Fact]
    public void 複製先の_ID_が空なら断る()
    {
        var source = CreateDefinition();

        Assert.Throws<ArgumentException>(() => SurveyDuplication.Copy(source, "  "));
    }
}
