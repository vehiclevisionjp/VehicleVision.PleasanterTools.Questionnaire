using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>選択肢と設問の並べ替え（Issue #103）。</summary>
/// <remarks>
/// **並べ替えそのものは画面側で行う。** サーバ側の役目は
/// **「並べ替えると壊れる組み合わせを公開の前に止めること」**だけなので、
/// ここで見るのもそれに限る。
/// </remarks>
public class ShuffleSettingsTests
{
    private static Question Radio(string id, QuestionSettings? settings = null) => new()
    {
        QuestionId = id,
        Type = QuestionType.Radio,
        Title = LocalizedText.Japanese(id),
        Choices = [new Choice("a", LocalizedText.Japanese("a"))],
        Settings = settings ?? new QuestionSettings(),
    };

    private static SurveyDefinition Definition(Page page) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages = [page],
    };

    private static SettingsProblemCode[] Codes(ImmutableArray<SettingsProblem> problems) =>
        [.. problems.Select(problem => problem.Code)];

    [Fact]
    public void 出し分けの条件を持つ設問があるページで設問の入れ替えを指定したら止める()
    {
        var conditional = Radio("q2") with
        {
            VisibleWhen = new VisibilityCondition
            {
                Match = ConditionMatch.All,
                Rules = [new ConditionRule("q1", ConditionOperator.Answered)],
            },
        };

        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions = [Radio("q1"), conditional],
            ShuffleQuestions = true,
        });

        var problems = QuestionSettingsValidator.Validate(definition);

        Assert.Equal([SettingsProblemCode.ShuffleBreaksVisibility], Codes(problems));
    }

    [Fact]
    public void 止めたときは原因の設問とページを指す()
    {
        var conditional = Radio("q2") with
        {
            VisibleWhen = new VisibilityCondition
            {
                Match = ConditionMatch.All,
                Rules = [new ConditionRule("q1", ConditionOperator.Answered)],
            },
        };

        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions = [Radio("q1"), conditional],
            ShuffleQuestions = true,
        });

        var problem = Assert.Single(QuestionSettingsValidator.Validate(definition));

        // **どこを直せばよいかが分からないと、公開できない理由が伝わらない**
        Assert.Equal("q2", problem.QuestionId);
        Assert.Equal("p1", problem.Detail);
    }

    [Fact]
    public void 条件が無ければ設問の入れ替えを通す()
    {
        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions = [Radio("q1"), Radio("q2")],
            ShuffleQuestions = true,
        });

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }

    [Fact]
    public void 入れ替えを指定していなければ条件があっても通す()
    {
        var conditional = Radio("q2") with
        {
            VisibleWhen = new VisibilityCondition
            {
                Match = ConditionMatch.All,
                Rules = [new ConditionRule("q1", ConditionOperator.Answered)],
            },
        };

        var definition = Definition(new Page
        {
            PageId = "p1",
            Questions = [Radio("q1"), conditional],
        });

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }

    [Fact]
    public void 選択肢を持たない設問で選択肢の入れ替えを指定したら止める()
    {
        var text = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Text,
            Title = LocalizedText.Japanese("自由入力"),
            Settings = new QuestionSettings { ShuffleChoices = true },
        };

        var definition = Definition(new Page { PageId = "p1", Questions = [text] });

        // **黙って無視すると「指定したのに効かない」と受け取られる**
        Assert.Equal([SettingsProblemCode.ShuffleWithoutChoices], Codes(QuestionSettingsValidator.Validate(definition)));
    }

    [Theory]
    [InlineData(QuestionType.Radio)]
    [InlineData(QuestionType.Checkbox)]
    [InlineData(QuestionType.Dropdown)]
    [InlineData(QuestionType.Grid)]
    [InlineData(QuestionType.Ranking)]
    public void 選択肢を持つ形式なら入れ替えを通す(QuestionType type)
    {
        var question = Radio("q1", new QuestionSettings { ShuffleChoices = true }) with
        {
            Type = type,
        };

        var definition = Definition(new Page { PageId = "p1", Questions = [question] });

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }

    [Fact]
    public void 説明文ブロックで選択肢の入れ替えを指定したら止める()
    {
        var note = new Question
        {
            QuestionId = "q1",
            Type = QuestionType.Note,
            Title = LocalizedText.Japanese("説明"),
            Settings = new QuestionSettings { ShuffleChoices = true },
        };

        var definition = Definition(new Page { PageId = "p1", Questions = [note] });

        Assert.Equal([SettingsProblemCode.ShuffleWithoutChoices], Codes(QuestionSettingsValidator.Validate(definition)));
    }
}
