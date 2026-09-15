using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>選べる数の下限・上限（Issue #101）。</summary>
public class SelectionCountTests
{
    private static Choice[] Choices(params string[] values) =>
        [.. values.Select(value => new Choice(value, LocalizedText.Japanese(value)))];

    private static Question Checkbox(QuestionSettings settings, bool required = false) => new()
    {
        QuestionId = "q1",
        Type = QuestionType.Checkbox,
        Title = LocalizedText.Japanese("好きなもの"),
        IsRequired = required,
        Choices = [.. Choices("a", "b", "c")],
        Settings = settings,
    };

    private static SurveyDefinition Definition(params Question[] questions) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages = [new Page { PageId = "p1", Questions = [.. questions] }],
    };

    private static ValidationErrorCode[] Codes(ImmutableArray<ValidationError> errors) =>
        [.. errors.Select(error => error.Code)];

    [Fact]
    public void 下限に足りなければ拒否する()
    {
        var definition = Definition(Checkbox(new QuestionSettings { MinSelections = 2 }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "a")]);

        Assert.Equal([ValidationErrorCode.TooFewSelections], Codes(errors));
    }

    [Fact]
    public void 上限を超えたら拒否する()
    {
        var definition = Definition(Checkbox(new QuestionSettings { MaxSelections = 2 }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "b", "c")]);

        Assert.Equal([ValidationErrorCode.TooManySelections], Codes(errors));
    }

    [Fact]
    public void 範囲に収まっていれば通す()
    {
        var definition = Definition(
            Checkbox(new QuestionSettings { MinSelections = 2, MaxSelections = 3 }));

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "b")]));
    }

    /// <remarks>
    /// **下限は「答えるなら下限まで」という意味。**
    /// 答えさせたいなら必須を立てる話であり、ここで代わりにしない。
    /// </remarks>
    [Fact]
    public void 任意の設問が未回答なら下限は効かない()
    {
        var definition = Definition(Checkbox(new QuestionSettings { MinSelections = 2 }));

        Assert.Empty(AnswerValidator.Validate(definition, []));
    }

    [Fact]
    public void 必須で未回答なら足りないではなく未回答として出す()
    {
        var definition = Definition(
            Checkbox(new QuestionSettings { MinSelections = 2 }, required: true));

        var errors = AnswerValidator.Validate(definition, []);

        Assert.Equal([ValidationErrorCode.Required], Codes(errors));
    }

    /// <remarks>**空白は数えない。** 数えると、空欄を送るだけで下限を満たせてしまう。</remarks>
    [Fact]
    public void 空白の値は数に入れない()
    {
        var definition = Definition(Checkbox(new QuestionSettings { MinSelections = 2 }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "   ")]);

        Assert.Contains(ValidationErrorCode.TooFewSelections, Codes(errors));
    }

    /// <remarks>**ランキングは対象外。** 並べた順が答えで、「いくつ選ぶか」とは別の話。</remarks>
    [Fact]
    public void ランキングには効かせない()
    {
        var definition = Definition(new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Ranking,
            Title = LocalizedText.Japanese("順位"),
            Choices = [.. Choices("a", "b", "c")],
            Settings = new QuestionSettings { MaxSelections = 1 },
        });

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "b")]));
    }

    /// <remarks>
    /// **グリッドは行ごとに数える。** 設問全体で数えると、行数ぶんだけ緩くなる。
    /// </remarks>
    [Fact]
    public void グリッドは行ごとに数える()
    {
        var definition = Definition(new Question
        {
            QuestionId = "q1",
            Type = QuestionType.CheckboxGrid,
            Title = LocalizedText.Japanese("行列"),
            Choices = [.. Choices("a", "b", "c")],
            Settings = new QuestionSettings
            {
                MaxSelections = 2,
                Rows =
                [
                    new GridRow("r1", LocalizedText.Japanese("1 行目")),
                    new GridRow("r2", LocalizedText.Japanese("2 行目")),
                ],
            },
        });

        // **2 行に分けて 4 つ選んでも、行ごとには上限内**
        var withinRows = new Answer("q1", [])
        {
            Rows = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("r1", ["a", "b"])
                .Add("r2", ["a", "b"]),
        };

        Assert.Empty(AnswerValidator.Validate(definition, [withinRows]));

        var overflowing = new Answer("q1", [])
        {
            Rows = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                .Add("r1", ["a", "b", "c"])
                .Add("r2", ["a"]),
        };

        Assert.Contains(
            ValidationErrorCode.TooManySelections,
            Codes(AnswerValidator.Validate(definition, [overflowing])));
    }

    [Fact]
    public void 答えようのない指定は公開の前に止める()
    {
        var reversed = Definition(
            Checkbox(new QuestionSettings { MinSelections = 3, MaxSelections = 2 }));
        Assert.Equal(
            [SettingsProblemCode.SelectionRangeReversed],
            QuestionSettingsValidator.Validate(reversed).Select(problem => problem.Code));

        // **選択肢は 3 つしか無い**
        var tooMany = Definition(Checkbox(new QuestionSettings { MinSelections = 4 }));
        Assert.Equal(
            [SettingsProblemCode.MinSelectionsExceedChoices],
            QuestionSettingsValidator.Validate(tooMany).Select(problem => problem.Code));

        var zero = Definition(Checkbox(new QuestionSettings { MaxSelections = 0 }));
        Assert.Equal(
            [SettingsProblemCode.SelectionRangeNotPositive],
            QuestionSettingsValidator.Validate(zero).Select(problem => problem.Code));

        var sane = Definition(
            Checkbox(new QuestionSettings { MinSelections = 1, MaxSelections = 3 }));
        Assert.Empty(QuestionSettingsValidator.Validate(sane));
    }

    /// <remarks>**上限が選択肢より多くても害は無い。** 届く数がそこまで増えないだけ。</remarks>
    [Fact]
    public void 上限が選択肢の数を超えていても止めない()
    {
        var definition = Definition(Checkbox(new QuestionSettings { MaxSelections = 99 }));

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }
}
