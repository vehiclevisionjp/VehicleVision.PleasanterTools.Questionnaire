using System.Collections.Immutable;
using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>フォーム定義の書き出し・取り込み（Issue #1009）。**DB を使わない。**</summary>
public class SurveyExchangeTests
{
    private const string HeaderImageId = "33333333-3333-4333-8333-333333333333";

    private static SurveyDefinition CreateDefinition() => new()
    {
        SurveyId = "11111111-1111-1111-1111-111111111111",
        Version = 7,
        Title = LocalizedText.Japanese("相談会"),
        Theme = new SurveyTheme { AccentColor = "#112233", HeaderImageId = HeaderImageId },
        Lookups =
        [
            new LookupDefinition
            {
                LookupId = "l1",
                AnchorQuestionId = "q1",
                SourceSiteId = 100,
                CredentialId = "secret-credential-ref",
            },
        ],
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
                        Title = LocalizedText.Japanese("参加しますか"),
                        Choices = [new Choice("yes", LocalizedText.Japanese("はい"))],
                    },
                ],
            },
        ],
    };

    private static string Export(SurveyDefinition? definition = null) =>
        SurveyExchange.Export(definition ?? CreateDefinition(), new MappingDefinition());

    [Fact]
    public void 資格情報の参照とヘッダ画像と版は書き出さない()
    {
        var json = Export();

        Assert.DoesNotContain("secret-credential-ref", json);
        Assert.DoesNotContain(HeaderImageId, json);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", json);

        var document = SurveyExchange.Parse(json).Document;
        Assert.NotNull(document);
        Assert.Equal(1, document.Definition.Version);
        Assert.Null(document.Definition.GetLookups()[0].CredentialId);
        Assert.Equal("#112233", document.Definition.Theme?.AccentColor);
        Assert.Null(document.Definition.Theme?.HeaderImageId);
    }

    [Fact]
    public void 書き出した内容を取り込むと設問と選択肢が戻る()
    {
        var parsed = SurveyExchange.Parse(Export());
        Assert.True(parsed.IsSuccess);

        var draft = SurveyExchange.ToDraft(parsed.Document!, "new-id");

        Assert.Equal("new-id", draft.SurveyId);
        // **複製と違い、題名に「のコピー」を付けない**
        Assert.Equal("相談会", draft.Title.Get("ja"));
        Assert.Equal("q1", Assert.Single(draft.AllQuestions).QuestionId);
        Assert.Equal("yes", Assert.Single(draft.AllQuestions).Choices[0].Value);
    }

    [Fact]
    public void 元の定義は変えない()
    {
        var source = CreateDefinition();

        _ = Export(source);

        Assert.Equal(HeaderImageId, source.Theme?.HeaderImageId);
        Assert.Equal("secret-credential-ref", source.GetLookups()[0].CredentialId);
    }

    [Fact]
    public void ヘッダ画像だけのテーマは既定へ戻る()
    {
        var source = CreateDefinition() with { Theme = new SurveyTheme { HeaderImageId = HeaderImageId } };

        Assert.Null(SurveyExchange.Portable(source).Theme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void 読めない入力は断る(string json)
    {
        Assert.Equal(SurveyExchangeError.Malformed, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 別の製品の_JSON_は断る()
    {
        Assert.Equal(
            SurveyExchangeError.WrongFormat,
            SurveyExchange.Parse("""{"format":"other","formatVersion":1}""").Error);
        Assert.Equal(
            SurveyExchangeError.WrongFormat,
            SurveyExchange.Parse("""{"definition":{}}""").Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void 読めない版は断る(int version)
    {
        var json = $$"""{"format":"{{SurveyExchange.FormatName}}","formatVersion":{{version}}}""";

        Assert.Equal(SurveyExchangeError.UnsupportedVersion, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 定義が無ければ断る()
    {
        var json = $$"""{"format":"{{SurveyExchange.FormatName}}","formatVersion":1}""";

        Assert.Equal(SurveyExchangeError.Malformed, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 大きすぎる入力は断る()
    {
        var json = new string('x', SurveyExchange.MaxBytes + 1);

        Assert.Equal(SurveyExchangeError.TooLarge, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 設問の識別子が重なれば断る()
    {
        var question = CreateDefinition().Pages[0].Questions[0];
        var definition = CreateDefinition() with
        {
            Pages = [new Page { PageId = "p1", Questions = [question, question] }],
        };

        var result = SurveyExchange.Parse(Export(definition));

        Assert.Equal(SurveyExchangeError.InvalidIdentifier, result.Error);
        Assert.Equal("q1", result.Detail);
    }

    [Fact]
    public void ページの識別子が重なる_または長すぎれば断る()
    {
        var duplicated = CreateDefinition() with
        {
            Pages = [new Page { PageId = "p1" }, new Page { PageId = "p1" }],
        };
        var tooLong = CreateDefinition() with
        {
            Pages = [new Page { PageId = new string('x', SurveyExchange.MaxIdentifierLength + 1) }],
        };

        Assert.Equal(SurveyExchangeError.InvalidIdentifier, SurveyExchange.Parse(Export(duplicated)).Error);
        Assert.Equal(SurveyExchangeError.InvalidIdentifier, SurveyExchange.Parse(Export(tooLong)).Error);
    }

    [Fact]
    public void ページ数が上限を超えれば断る()
    {
        var definition = CreateDefinition() with
        {
            Pages = [.. Enumerable.Range(0, SurveyExchange.MaxPages + 1)
                .Select(index => new Page { PageId = $"p{index}" })],
        };

        Assert.Equal(SurveyExchangeError.TooManyItems, SurveyExchange.Parse(Export(definition)).Error);
    }

    [Fact]
    public void 配列の欄が_null_でも落ちずに断る()
    {
        var json = $$$"""
            {"format":"{{{SurveyExchange.FormatName}}}","formatVersion":1,
             "definition":{"surveyId":"x","version":1,"title":{"ja":"a"},"pages":null}}
            """;

        Assert.Equal(SurveyExchangeError.Malformed, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 未知の列挙値は断る()
    {
        var json = Export().Replace("\"Radio\"", "\"NoSuchType\"", StringComparison.Ordinal);

        Assert.Equal(SurveyExchangeError.Malformed, SurveyExchange.Parse(json).Error);
    }

    [Fact]
    public void 書き出しは取り込みで使わない一覧を持つ()
    {
        using var document = JsonDocument.Parse(Export());

        Assert.Equal(SurveyExchange.FormatName, document.RootElement.GetProperty("format").GetString());
        Assert.Contains(
            document.RootElement.GetProperty("omitted").EnumerateArray().Select(item => item.GetString()),
            item => item == "credentials");
    }

    [Fact]
    public void 記法が参照する資産の識別子を数える()
    {
        var assetId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var definition = CreateDefinition() with
        {
            ConfirmationMessage = LocalizedText.Japanese($"![図](asset:{assetId})"),
        };

        Assert.Equal([assetId], SurveyAssetReferences.ReferencedAssetIds(definition));
    }
}
