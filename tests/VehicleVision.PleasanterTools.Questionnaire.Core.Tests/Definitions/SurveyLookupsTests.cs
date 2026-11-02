using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>定義のルックアップ（Issue #813）。**既存の JSON を 1 バイトも変えない。**</summary>
public class SurveyLookupsTests
{
    /// <summary>
    /// ルックアップを足す前の実装が書いた JSON（2026-11-02、<c>develop</c> 相当）。
    /// **この文字列との一致が「既存の公開済みの版の JSON が変わらない」の根拠。**
    /// </summary>
    private const string BeforeLookupsJson =
        """{"surveyId":"s1","version":3,"title":{"ja":"満足度"},"fallbackLanguage":"ja","pages":[{"pageId":"p1","questions":[{"questionId":"q1","type":"Text","title":{"ja":"お名前"},"isRequired":true,"choices":[],"settings":{"descriptionFormat":"Plain","convertFullWidthAsciiToHalfWidth":false,"convertHalfWidthKanaToFullWidth":false,"convertFullWidthSpacesToHalfWidth":false,"trimWhitespace":false,"shuffleChoices":false,"rows":[]},"hasChoiceTransitions":false,"canCarryTransitions":false,"hasChoices":false,"hasRows":false,"hasRowPorts":false,"rowPortIds":[],"isMultiValue":false,"hasSelectionRange":false,"isDisplayOnly":false}],"shuffleQuestions":false}],"displayMode":"Paged","showProgress":true,"allowEditingAfterSubmit":true,"allQuestions":[{"questionId":"q1","type":"Text","title":{"ja":"お名前"},"isRequired":true,"choices":[],"settings":{"descriptionFormat":"Plain","convertFullWidthAsciiToHalfWidth":false,"convertHalfWidthKanaToFullWidth":false,"convertFullWidthSpacesToHalfWidth":false,"trimWhitespace":false,"shuffleChoices":false,"rows":[]},"hasChoiceTransitions":false,"canCarryTransitions":false,"hasChoices":false,"hasRows":false,"hasRowPorts":false,"rowPortIds":[],"isMultiValue":false,"hasSelectionRange":false,"isDisplayOnly":false}]}""";

    private static SurveyDefinition Plain() => new()
    {
        SurveyId = "s1",
        Version = 3,
        Title = LocalizedText.Japanese("満足度"),
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
                        Title = LocalizedText.Japanese("お名前"),
                        IsRequired = true,
                    },
                ],
            },
        ],
    };

    private static LookupDefinition Lookup() => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "q1",
        SourceSiteId = 10,
        CredentialId = "cred-1",
        Keys = [new LookupKey { QuestionId = "q1", SourceColumn = "ClassA" }],
        Targets = [new LookupTarget { SourceColumn = "ClassB", QuestionId = "q1" }],
        AllowedColumns = ["ClassB"],
        MaxResults = 5,
    };

    [Fact]
    public void ルックアップが無い定義のJSONは変わらない()
    {
        Assert.Equal(BeforeLookupsJson, SurveyJson.Serialize(Plain()));
    }

    [Fact]
    public void ルックアップが無い過去のJSONは空として読める()
    {
        var restored = SurveyJson.Deserialize<SurveyDefinition>(BeforeLookupsJson);

        Assert.NotNull(restored);
        Assert.Null(restored.Lookups);
        Assert.Empty(restored.GetLookups());
    }

    [Fact]
    public void ルックアップを書いて読み戻せる()
    {
        var json = SurveyJson.Serialize(Plain() with { Lookups = [Lookup()] });

        var restored = SurveyJson.Deserialize<SurveyDefinition>(json);

        Assert.Contains("\"lookups\":[", json);
        Assert.NotNull(restored);
        var lookup = Assert.Single(restored.GetLookups());
        Assert.Equal("l1", lookup.LookupId);
        Assert.Equal(LookupSourceKind.Pleasanter, lookup.SourceKind);
        Assert.Equal(10, lookup.SourceSiteId);
        Assert.Equal("cred-1", lookup.CredentialId);
        Assert.Equal("ClassA", Assert.Single(lookup.Keys).SourceColumn);
        Assert.Equal("ClassB", Assert.Single(lookup.Targets).SourceColumn);
        Assert.Equal(5, lookup.MaxResults);
    }

    [Fact]
    public void 列挙は文字列で書く()
    {
        var json = SurveyJson.Serialize(Plain() with { Lookups = [Lookup()] });

        Assert.Contains("\"sourceKind\":\"Pleasanter\"", json);
        Assert.Contains("\"matchMode\":\"Exact\"", json);
    }

    [Fact]
    public void 複製では資格情報の参照を引き継がない()
    {
        var source = Plain() with { Lookups = [Lookup()] };

        var copy = SurveyDuplication.Copy(source, "s2");

        var lookup = Assert.Single(copy.GetLookups());
        Assert.Null(lookup.CredentialId);
        Assert.Equal("l1", lookup.LookupId);
        Assert.Equal(10, lookup.SourceSiteId);
        Assert.Equal("cred-1", Assert.Single(source.GetLookups()).CredentialId);
    }

    [Fact]
    public void ルックアップが無い定義の複製はルックアップを持たない()
    {
        Assert.Null(SurveyDuplication.Copy(Plain(), "s2").Lookups);
    }
}
