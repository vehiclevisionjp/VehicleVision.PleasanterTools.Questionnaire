using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>設問の説明文の設定（Issue #267）。</summary>
public sealed class DescriptionSettingsTests
{
    private static SurveyDefinition Definition(string description) => new()
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
                        QuestionId = "q1",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("設問"),
                        Description = LocalizedText.Japanese(description),
                    },
                ],
            },
        ],
    };

    [Fact]
    public void 説明文は上限ちょうどまで公開できる()
    {
        var definition = Definition(new string('あ', NoteMarkup.MaximumLength));

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }

    [Fact]
    public void 説明文が上限を超えると公開できない()
    {
        var definition = Definition(new string('あ', NoteMarkup.MaximumLength + 1));

        var problem = Assert.Single(QuestionSettingsValidator.Validate(definition));

        Assert.Equal(SettingsProblemCode.DescriptionTooLong, problem.Code);
        Assert.Equal("q1", problem.QuestionId);
    }
}
