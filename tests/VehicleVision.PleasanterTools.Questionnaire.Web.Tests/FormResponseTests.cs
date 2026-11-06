using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答画面へ返す応答の組み立て（Issue #874）。</summary>
public sealed class FormResponseTests
{
    private static SurveyDefinition Definition(bool withLookup) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("題名"),
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question { QuestionId = "a1", Type = QuestionType.Text, Title = LocalizedText.Japanese("a1") },
                    new Question { QuestionId = "k1", Type = QuestionType.Text, Title = LocalizedText.Japanese("k1") },
                ],
            },
        ],
        Lookups = withLookup
            ?
            [
                new LookupDefinition
                {
                    LookupId = "l1",
                    AnchorQuestionId = "a1",
                    SourceSiteId = 12345,
                    CredentialId = "secret-credential",
                    Keys = [new LookupKey
                    {
                        QuestionId = "k1",
                        SourceColumn = "ClassA",
                        SourceColumnKind = LookupColumnKind.Text,
                    }],
                    Targets = [new LookupTarget { QuestionId = "a1", SourceColumn = "ClassB" }],
                    AllowedColumns = ["ClassB"],
                    MaxResults = 7,
                    DisplayFields = [new LookupDisplayField { DisplayId = "city", SourceColumn = "ClassB" }],
                },
            ]
            : null,
    };

    private static string Serialize(FormResponse response) =>
        JsonSerializer.Serialize(response, SurveyJson.Options);

    [Fact]
    public void 公開用のlookupsを最上位に載せ_definitionから生のlookupsを除く()
    {
        var response = FormResponse.Create("pub", Definition(withLookup: true), requiresProofOfWork: false);

        var lookup = Assert.Single(response.Lookups!.Value);
        Assert.Equal("l1", lookup.LookupId);
        Assert.Null(response.Definition.Lookups);

        var text = Serialize(response);
        foreach (var leaked in new[]
        {
            "sourceSiteId", "12345", "credentialId", "secret-credential", "sourceColumn",
            "allowedColumns", "maxResults", "allowPrefix", "ClassA", "ClassB",
        })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }

        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.TryGetProperty("lookups", out _));
        Assert.False(json.RootElement.GetProperty("definition").TryGetProperty("lookups", out _));
    }

    [Fact]
    public void ルックアップが無ければlookupsを省く()
    {
        var response = FormResponse.Create("pub", Definition(withLookup: false), requiresProofOfWork: true);

        Assert.Null(response.Lookups);
        using var json = JsonDocument.Parse(Serialize(response));
        Assert.False(json.RootElement.TryGetProperty("lookups", out _));
        Assert.True(json.RootElement.GetProperty("requiresProofOfWork").GetBoolean());
    }
}
