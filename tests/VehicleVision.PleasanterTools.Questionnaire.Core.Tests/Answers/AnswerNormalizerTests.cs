using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Answers;

public class AnswerNormalizerTests
{
    private static QuestionSettings AllEnabled() => new()
    {
        ConvertFullWidthAsciiToHalfWidth = true,
        ConvertHalfWidthKanaToFullWidth = true,
        ConvertFullWidthSpacesToHalfWidth = true,
        TrimWhitespace = true,
    };

    [Fact]
    public void 四種類の変換を組み合わせられる()
    {
        var actual = AnswerNormalizer.Normalize(
            "　ＡＢＣ１２３－４５６７　ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ　",
            AllEnabled());

        Assert.Equal("ABC123-4567 パピプペポ", actual);
    }

    [Fact]
    public void 各変換は独立して有効にできる()
    {
        Assert.Equal(
            "ABC",
            AnswerNormalizer.Normalize(
                "ＡＢＣ",
                new QuestionSettings { ConvertFullWidthAsciiToHalfWidth = true }));
        Assert.Equal(
            "パピ",
            AnswerNormalizer.Normalize(
                "ﾊﾟﾋﾟ",
                new QuestionSettings { ConvertHalfWidthKanaToFullWidth = true }));
        Assert.Equal(
            " A B ",
            AnswerNormalizer.Normalize(
                "　A　B　",
                new QuestionSettings { ConvertFullWidthSpacesToHalfWidth = true }));
        Assert.Equal(
            "Ａ　ﾊﾟ",
            AnswerNormalizer.Normalize(
                "　Ａ　ﾊﾟ　",
                new QuestionSettings { TrimWhitespace = true }));
    }

    [Fact]
    public void NFKCで変わる互換文字は変更しない()
    {
        Assert.Equal("㍿①Ⅳ㎡", AnswerNormalizer.Normalize("㍿①Ⅳ㎡", AllEnabled()));
    }

    [Fact]
    public void 設定が無ければ既存の回答を変更しない()
    {
        const string value = "　ＡＢＣ ﾊﾟﾋﾟ　";

        Assert.Equal(value, AnswerNormalizer.Normalize(value, new QuestionSettings()));
    }

    [Fact]
    public void 変換後の値は正規表現と形式の検査を通る()
    {
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Pages =
            [
                new Page
                {
                    PageId = "p1",
                    Questions =
                    [
                        new Question
                        {
                            QuestionId = "postal",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("郵便番号"),
                            Settings = AllEnabled() with { Pattern = "[0-9]{3}-[0-9]{4}" },
                        },
                        new Question
                        {
                            QuestionId = "mail",
                            Type = QuestionType.Text,
                            Title = LocalizedText.Japanese("メール"),
                            Settings = AllEnabled() with { Format = TextFormat.Email },
                        },
                    ],
                },
            ],
        };
        var normalized = AnswerNormalizer.Normalize(
            definition,
            [
                Answer.Of("postal", "１２３－４５６７"),
                Answer.Of("mail", "ｕｓｅｒ＠ｅｘａｍｐｌｅ．ｃｏｍ"),
            ]);

        Assert.Empty(AnswerValidator.Validate(definition, normalized));
    }
}
