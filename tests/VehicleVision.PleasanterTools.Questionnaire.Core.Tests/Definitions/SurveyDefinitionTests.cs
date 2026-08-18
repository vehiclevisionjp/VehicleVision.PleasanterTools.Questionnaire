using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

public class SurveyDefinitionTests
{
    private static SurveyDefinition CreateDefinition() => new()
    {
        SurveyId = "s1",
        Version = 3,
        Title = LocalizedText.Japanese("顧客満足度アンケート"),
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
                        Type = QuestionType.Radio,
                        Title = LocalizedText.Japanese("ご満足いただけましたか"),
                        Choices =
                        [
                            new Choice("5", LocalizedText.Japanese("とても満足")),
                            new Choice("1", LocalizedText.Japanese("とても不満")),
                        ],
                    },
                    new Question
                    {
                        QuestionId = "note1",
                        Type = QuestionType.Note,
                        Title = LocalizedText.Japanese("ここから設問です"),
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
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                ],
            },
        ],
    };

    [Fact]
    public void 全ページの設問をページ順に返す()
    {
        var definition = CreateDefinition();

        Assert.Equal(["q1", "note1", "q2"], definition.AllQuestions.Select(q => q.QuestionId));
    }

    [Fact]
    public void ページをまたいで設問を引ける()
    {
        var definition = CreateDefinition();

        Assert.Equal(QuestionType.Paragraph, definition.FindQuestion("q2")?.Type);
    }

    [Fact]
    public void 存在しない設問はnullを返す()
    {
        var definition = CreateDefinition();

        Assert.Null(definition.FindQuestion("存在しない"));
    }

    [Fact]
    public void 説明文ブロックは表示専用として扱う()
    {
        // Note は Pleasanter の列へ写さない。マッピングを持たないことで表現する
        var definition = CreateDefinition();

        Assert.True(definition.FindQuestion("note1")!.IsDisplayOnly);
        Assert.False(definition.FindQuestion("q1")!.IsDisplayOnly);
    }

    [Fact]
    public void 選択肢を持つ形式かを判定できる()
    {
        var definition = CreateDefinition();

        Assert.True(definition.FindQuestion("q1")!.HasChoices);
        Assert.False(definition.FindQuestion("q2")!.HasChoices);
    }
}
