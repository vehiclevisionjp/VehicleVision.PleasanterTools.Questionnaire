using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

public class SurveyJsonTests
{
    private static SurveyDefinition Definition() => new()
    {
        SurveyId = "s1",
        Version = 7,
        Title = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "顧客満足度アンケート",
            ["en"] = "Customer Satisfaction",
        }),
        Description = LocalizedText.Japanese("ご協力ください🙏"),
        DisplayMode = DisplayMode.OneQuestionPerPage,
        ShowProgress = false,
        AssetDelivery = new AssetDeliverySettings
        {
            Expiration = AssetTicketExpiration.AcceptTo,
            Days = 45,
        },
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Title = LocalizedText.Japanese("基本情報"),
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
                            new Choice("5", LocalizedText.Japanese("とても満足")),
                            new Choice("other", LocalizedText.Japanese("その他"), IsOther: true),
                        ],
                        Settings = new QuestionSettings
                        {
                            MaxLength = 100,
                            DescriptionFormat = DescriptionFormat.Markup,
                            ConvertFullWidthAsciiToHalfWidth = true,
                            ConvertHalfWidthKanaToFullWidth = true,
                            ConvertFullWidthSpacesToHalfWidth = true,
                            TrimWhitespace = true,
                            Format = TextFormat.Email,
                            ScaleMinimum = 1,
                            ScaleMaximum = 5,
                            MaxFileSizeBytes = 1024,
                        },
                    },
                    new Question
                    {
                        QuestionId = "note1",
                        Type = QuestionType.Note,
                        Title = LocalizedText.Japanese("ここから設問です"),
                    },
                ],
            },
        ],
    };

    private static MappingDefinition Mapping() => new()
    {
        Assignments =
        [
            ColumnAssignment.Direct("ClassA", new MappingSource("q1")),
            ColumnAssignment.Converted(
                "ClassB",
                MappingConverter.Of(ConverterOperations.Join, ("separator", " / ")),
                new MappingSource("q1"),
                new MappingSource("q1", QuestionPort.OtherText)),
        ],
    };

    [Fact]
    public void 定義をJSONへ書いて読み戻せる()
    {
        var json = SurveyJson.Serialize(Definition());

        var restored = SurveyJson.Deserialize<SurveyDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(7, restored.Version);
        Assert.Equal(DisplayMode.OneQuestionPerPage, restored.DisplayMode);
        Assert.False(restored.ShowProgress);
        Assert.Equal(AssetTicketExpiration.AcceptTo, restored.AssetDelivery!.Expiration);
        Assert.Equal(45, restored.AssetDelivery.Days);
        Assert.Equal("顧客満足度アンケート", restored.Title.Get("ja"));
        Assert.Equal("Customer Satisfaction", restored.Title.Get("en"));
        Assert.Equal("ご協力ください🙏", restored.Description!.Get("ja"));

        var question = restored.FindQuestion("q1");
        Assert.NotNull(question);
        Assert.Equal(QuestionType.Radio, question.Type);
        Assert.True(question.IsRequired);
        Assert.Equal(2, question.Choices.Length);
        Assert.True(question.Choices[1].IsOther);
        Assert.Equal(100, question.Settings.MaxLength);
        Assert.Equal(DescriptionFormat.Markup, question.Settings.DescriptionFormat);
        Assert.True(question.Settings.ConvertFullWidthAsciiToHalfWidth);
        Assert.True(question.Settings.ConvertHalfWidthKanaToFullWidth);
        Assert.True(question.Settings.ConvertFullWidthSpacesToHalfWidth);
        Assert.True(question.Settings.TrimWhitespace);
        Assert.Equal(TextFormat.Email, question.Settings.Format);
        Assert.True(restored.FindQuestion("note1")!.IsDisplayOnly);
    }

    [Fact]
    public void マッピングをJSONへ書いて読み戻せる()
    {
        var json = SurveyJson.Serialize(Mapping());

        var restored = SurveyJson.Deserialize<MappingDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Assignments.Length);

        var direct = restored.Assignments[0];
        Assert.Equal("ClassA", direct.TargetColumn);
        Assert.Null(direct.Converter);
        Assert.True(direct.HasValidShape);

        var converted = restored.Assignments[1];
        Assert.Equal(ConverterOperations.Join, converted.Converter!.Operation);
        Assert.Equal(" / ", converted.Converter.Config["separator"]);
        Assert.Equal(QuestionPort.OtherText, converted.Sources[1].Port);
    }

    [Fact]
    public void 列挙は文字列で書く()
    {
        // **数値だと、列挙に値を挿入したときに過去の版の意味が変わる**
        var json = SurveyJson.Serialize(Definition());

        Assert.Contains("\"Radio\"", json);
        Assert.Contains("\"OneQuestionPerPage\"", json);
        Assert.DoesNotContain("\"type\":2", json);
        Assert.Contains("\"descriptionFormat\":\"Markup\"", json);
    }

    [Fact]
    public void 過去のJSONに書き方が無ければプレーンになる()
    {
        const string json =
            """
            {
              "questionId": "q1",
              "type": "Text",
              "title": { "ja": "設問" },
              "description": { "ja": "**そのまま**" },
              "settings": {}
            }
            """;

        var question = SurveyJson.Deserialize<Question>(json);

        Assert.NotNull(question);
        Assert.Equal(DescriptionFormat.Plain, question.Settings.DescriptionFormat);
        Assert.Null(question.DescriptionBlocks);
        Assert.False(question.Settings.ConvertFullWidthAsciiToHalfWidth);
        Assert.False(question.Settings.ConvertHalfWidthKanaToFullWidth);
        Assert.False(question.Settings.ConvertFullWidthSpacesToHalfWidth);
        Assert.False(question.Settings.TrimWhitespace);
    }

    [Fact]
    public void 確認は埋め込みの後ろへ追加されている()
    {
        Assert.Equal((int)QuestionType.Embed + 1, (int)QuestionType.Confirm);
    }

    [Fact]
    public void 多言語は言語コードのオブジェクトで書く()
    {
        var json = SurveyJson.Serialize(LocalizedText.Japanese("こんにちは"));

        Assert.Equal("{\"ja\":\"こんにちは\"}", json);
    }

    [Fact]
    public void 読めないJSONはnullを返す()
    {
        // **送信ワーカーはこれをデッドレターとして扱う**
        Assert.Null(SurveyJson.Deserialize<SurveyDefinition>("これは JSON ではない"));
    }
}
