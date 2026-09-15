using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

public class AnswerValidatorTests
{
    private static Question Q(
        string id,
        QuestionType type,
        bool required = false,
        QuestionSettings? settings = null,
        params Choice[] choices) => new()
        {
            QuestionId = id,
            Type = type,
            Title = LocalizedText.Japanese(id),
            IsRequired = required,
            Choices = [.. choices],
            Settings = settings ?? new QuestionSettings(),
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
    public void 必須が未回答ならエラー()
    {
        var definition = Definition(Q("q1", QuestionType.Text, required: true));

        var errors = AnswerValidator.Validate(definition, []);

        Assert.Equal([ValidationErrorCode.Required], Codes(errors));
    }

    [Fact]
    public void 空白だけの回答は未回答として扱う()
    {
        var definition = Definition(Q("q1", QuestionType.Text, required: true));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "   ")]);

        Assert.Equal([ValidationErrorCode.Required], Codes(errors));
    }

    [Fact]
    public void 任意の設問が未回答でもエラーにしない()
    {
        var definition = Definition(Q("q1", QuestionType.Text));

        Assert.Empty(AnswerValidator.Validate(definition, []));
    }

    [Fact]
    public void 定義に無い設問への回答は受け取らない()
    {
        var definition = Definition(Q("q1", QuestionType.Text));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("存在しない", "x")]);

        Assert.Equal([ValidationErrorCode.UnknownQuestion], Codes(errors));
    }

    [Fact]
    public void 説明文ブロックへの回答は受け取らない()
    {
        var definition = Definition(Q("note1", QuestionType.Note));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("note1", "x")]);

        Assert.Equal([ValidationErrorCode.AnswerNotAllowed], Codes(errors));
    }

    [Fact]
    public void 単一選択に複数の値が来たら拒否する()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Radio, choices:
            [new Choice("a", LocalizedText.Japanese("A")), new Choice("b", LocalizedText.Japanese("B"))]));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "b")]);

        Assert.Equal([ValidationErrorCode.MultipleValuesNotAllowed], Codes(errors));
    }

    [Fact]
    public void 複数選択なら複数の値を受け取る()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Checkbox, choices:
            [new Choice("a", LocalizedText.Japanese("A")), new Choice("b", LocalizedText.Japanese("B"))]));

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "a", "b")]));
    }

    [Fact]
    public void 選択肢に無い値は拒否する()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Radio, choices: [new Choice("a", LocalizedText.Japanese("A"))]));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "z")]);

        Assert.Equal([ValidationErrorCode.UnknownChoice], Codes(errors));
        Assert.Equal("z", errors.Single().Detail);
    }

    [Fact]
    public void その他を選んでいなければ自由記述を受け取らない()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Radio, choices: [new Choice("a", LocalizedText.Japanese("A"))]));
        var answer = new Answer("q1", ["a"]) { OtherText = "勝手に書いた" };

        var errors = AnswerValidator.Validate(definition, [answer]);

        Assert.Equal([ValidationErrorCode.OtherTextNotAllowed], Codes(errors));
    }

    [Fact]
    public void その他を選んでいれば自由記述を受け取る()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Radio, choices:
            [new Choice("other", LocalizedText.Japanese("その他"), IsOther: true)]));
        var answer = new Answer("q1", ["other"]) { OtherText = "自由記述" };

        Assert.Empty(AnswerValidator.Validate(definition, [answer]));
    }

    [Fact]
    public void 文字数の上限を超えたら拒否する()
    {
        var definition = Definition(Q(
            "q1", QuestionType.Text, settings: new QuestionSettings { MaxLength = 3 }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "あいうえお")]);

        Assert.Equal([ValidationErrorCode.TooLong], Codes(errors));
        Assert.Equal("3", errors.Single().Detail);
    }

    [Theory]
    [InlineData("user@example.com", 0)]
    [InlineData("not-an-email", 1)]
    public void メール形式を検証する(string value, int expectedErrorCount)
    {
        var definition = Definition(Q(
            "q1", QuestionType.Text, settings: new QuestionSettings { Format = TextFormat.Email }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", value)]);

        Assert.Equal(expectedErrorCount, errors.Length);
    }

    [Theory]
    [InlineData("https://example.com", 0)]
    [InlineData("example.com", 1)]
    [InlineData("javascript:alert(1)", 1)]
    public void URL形式を検証する(string value, int expectedErrorCount)
    {
        // **http / https 以外を通さない。** javascript: を通すと画面側で危険
        var definition = Definition(Q(
            "q1", QuestionType.Text, settings: new QuestionSettings { Format = TextFormat.Url }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", value)]);

        Assert.Equal(expectedErrorCount, errors.Length);
    }

    [Theory]
    [InlineData("3", 0)]
    [InlineData("0", 1)]
    [InlineData("6", 1)]
    public void 尺度の範囲を検証する(string value, int expectedErrorCount)
    {
        var definition = Definition(Q(
            "q1",
            QuestionType.Scale,
            settings: new QuestionSettings { ScaleMinimum = 1, ScaleMaximum = 5 }));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", value)]);

        Assert.Equal(expectedErrorCount, errors.Length);
    }

    [Fact]
    public void 尺度に数値でない値が来たら拒否する()
    {
        var definition = Definition(Q("q1", QuestionType.Scale));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "とても満足")]);

        Assert.Equal([ValidationErrorCode.NotANumber], Codes(errors));
    }

    [Theory]
    [InlineData("2026-03-01", 0)]
    [InlineData("2026-13-45", 1)]
    public void 日付を検証する(string value, int expectedErrorCount)
    {
        var definition = Definition(Q("q1", QuestionType.Date));

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", value)]);

        Assert.Equal(expectedErrorCount, errors.Length);
    }

    [Fact]
    public void ページ指定なら他のページの必須は見ない()
    {
        // 必須チェックはページ遷移時にそのページ分だけ。最後にまとめて出さない
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages =
            [
                new Page { PageId = "p1", Questions = [Q("q1", QuestionType.Text, required: true)] },
                new Page { PageId = "p2", Questions = [Q("q2", QuestionType.Text, required: true)] },
            ],
        };

        var errors = AnswerValidator.ValidatePage(definition, "p1", [Answer.Of("q1", "回答")]);

        Assert.Empty(errors);
    }

    [Fact]
    public void 添付が必須の設問はファイルが無ければエラー()
    {
        var definition = Definition(Q("q1", QuestionType.File, required: true));

        var errors = AnswerValidator.Validate(definition, [new Answer("q1", [])]);

        Assert.Equal([ValidationErrorCode.Required], Codes(errors));
    }

    [Fact]
    public void 添付の設問は値が無くてもファイルがあれば通る()
    {
        // **添付の設問は Values を持たない。** 値の有無で見ると必ず未回答になってしまう
        var definition = Definition(Q("q1", QuestionType.File, required: true));

        var errors = AnswerValidator.Validate(
            definition, [new Answer("q1", []) { FileNames = ["a.png"] }]);

        Assert.Empty(errors);
    }
}
