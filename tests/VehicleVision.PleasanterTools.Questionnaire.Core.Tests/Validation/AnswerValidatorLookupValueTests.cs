using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>ルックアップの取得値を入力先の設問で検証する（Issue #832）。</summary>
public sealed class AnswerValidatorLookupValueTests
{
    private static Question Text(int? maxLength = null, bool required = true) => new()
    {
        QuestionId = "q",
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese("q"),
        IsRequired = required,
        Settings = new QuestionSettings { MaxLength = maxLength },
    };

    [Fact]
    public void 規則に合う値は誤りなし_必須は見ない()
    {
        Assert.Empty(AnswerValidator.ValidateLookupValue(Text(maxLength: 3), "abc"));
    }

    [Fact]
    public void 文字数の超過は誤りになる()
    {
        var error = Assert.Single(AnswerValidator.ValidateLookupValue(Text(maxLength: 2), "abc"));
        Assert.Equal(ValidationErrorCode.TooLong, error.Code);
    }

    [Fact]
    public void 選択肢に無い値は誤りになり_ある値は通る()
    {
        var radio = new Question
        {
            QuestionId = "q",
            Type = QuestionType.Radio,
            Title = LocalizedText.Japanese("q"),
            Choices = [new Choice("東京", LocalizedText.Japanese("東京"))],
        };

        Assert.Empty(AnswerValidator.ValidateLookupValue(radio, "東京"));
        Assert.Equal(
            ValidationErrorCode.UnknownChoice,
            Assert.Single(AnswerValidator.ValidateLookupValue(radio, "大阪")).Code);
    }
}
