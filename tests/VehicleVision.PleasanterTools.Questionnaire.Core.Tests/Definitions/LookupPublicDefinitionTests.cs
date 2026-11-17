using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>回答画面用のルックアップ定義（Issue #822）。**秘密と参照元の構造を含めない。**</summary>
public class LookupPublicDefinitionTests
{
    private static LookupDefinition Lookup() => new()
    {
        LookupId = "l1",
        AnchorQuestionId = "btn",
        ButtonLabel = LocalizedText.Japanese("住所を検索"),
        SourceSiteId = 424242,
        CredentialId = "credential-secret-ref",
        Keys =
        [
            new LookupKey { QuestionId = "k1", SourceColumn = "ClassHiddenKey" },
            new LookupKey { QuestionId = "k2", SourceColumn = "ClassHiddenKey2", AllowPrefix = true },
        ],
        Targets = [new LookupTarget { SourceColumn = "ClassHiddenTarget", QuestionId = "t1" }],
        AllowedColumns = ["ClassHiddenTarget", "ClassHiddenOther"],
        DisplayFields =
        [
            new LookupDisplayField
            {
                DisplayId = "city",
                SourceColumn = "ClassHiddenOther",
                Heading = LocalizedText.Japanese("市区町村"),
            },
        ],
        MaxResults = 17,
    };

    [Fact]
    public void 操作に必要な情報だけを写す()
    {
        var dto = LookupPublicDefinition.From(Lookup());

        Assert.Equal("l1", dto.LookupId);
        Assert.Equal("btn", dto.AnchorQuestionId);
        Assert.Equal("住所を検索", dto.ButtonLabel!.Get("ja"));
        Assert.Equal(["k1", "k2"], dto.KeyQuestionIds.ToArray());
        Assert.Equal(["t1"], dto.TargetQuestionIds.ToArray());
        var field = Assert.Single(dto.DisplayFields);
        Assert.Equal("city", field.DisplayId);
        Assert.Equal("市区町村", field.Heading!.Get("ja"));
    }

    [Fact]
    public void JSONに参照元の構造も資格情報も含まれない()
    {
        var json = SurveyJson.Serialize(LookupPublicDefinition.From(Lookup()));

        foreach (var forbidden in new[]
        {
            "424242", "credential", "Credential", "ClassHidden", "sourceSiteId",
            "sourceKind", "sourceColumn", "allowedColumns", "displayColumns",
            "maxResults", "allowPrefix", "matchMode", "17",
        })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 定義にルックアップが無ければ空()
    {
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
        };

        Assert.Empty(LookupPublicDefinition.FromDefinition(definition));
    }

    [Fact]
    public void 定義のルックアップを順に写す()
    {
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = LocalizedText.Japanese("検証用"),
            Lookups = [Lookup(), Lookup() with { LookupId = "l2", AnchorQuestionId = "btn2" }],
        };

        var dtos = LookupPublicDefinition.FromDefinition(definition);

        Assert.Equal(["l1", "l2"], dtos.Select(dto => dto.LookupId).ToArray());
    }
}
