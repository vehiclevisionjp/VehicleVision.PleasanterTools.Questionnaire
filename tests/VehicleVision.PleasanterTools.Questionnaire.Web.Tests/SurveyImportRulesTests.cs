using System.Collections.Frozen;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>
/// インポートが通る検査（Issue #1009）。**公開前の検査と保存の関所を共有していること**を確かめる。
/// </summary>
public class SurveyImportRulesTests
{
    private static EmbedOptions NoEmbeds() =>
        EmbedOptions.FromSnapshot(new AppSettingsSnapshot(
            [],
            new Dictionary<string, string> { [EmbedOptions.AllowedHostsKey] = string.Empty }
                .ToFrozenDictionary(StringComparer.Ordinal),
            FrozenSet<string>.Empty));

    private static SurveyDefinition Definition(params Question[] questions) => new()
    {
        SurveyId = "x",
        Version = 1,
        Title = LocalizedText.Japanese("題名"),
        Pages = [new Page { PageId = "p1", Questions = [.. questions] }],
    };

    private static Question Text(string id) => new()
    {
        QuestionId = id,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese(id),
    };

    [Fact]
    public void 設問が無い定義は公開前の検査と同じく止まる()
    {
        var inspection = PublishInspector.Inspect(Definition(), new MappingDefinition());

        Assert.True(inspection.NoAnswerableQuestion);
        Assert.True(inspection.HasBlocking);
    }

    [Fact]
    public void 答えられる設問があり不備が無ければ通る()
    {
        var inspection = PublishInspector.Inspect(Definition(Text("q1")), new MappingDefinition());

        Assert.False(inspection.NoAnswerableQuestion);
        Assert.Empty(inspection.Flow);
        Assert.Empty(inspection.Settings);
    }

    [Fact]
    public void 存在しないページへの分岐は止まる()
    {
        var definition = Definition(Text("q1")) with
        {
            Pages = [new Page
            {
                PageId = "p1",
                Next = PageTransition.To("missing"),
                Questions = [Text("q1")],
            }],
        };

        Assert.True(PublishInspector.Inspect(definition, new MappingDefinition()).HasBlocking);
    }

    [Fact]
    public void 形の違う色は保存の関所で断る()
    {
        var definition = Definition(Text("q1")) with { Theme = new SurveyTheme { AccentColor = "red; x" } };

        var violation = DefinitionSaveRules.Check(definition, NoEmbeds());

        Assert.Equal(ServerMessageKeys.ThemeColorInvalid, violation?.MessageKey);
    }

    [Fact]
    public void 許していない埋め込み先は断る()
    {
        var embed = new Question
        {
            QuestionId = "e1",
            Type = QuestionType.Embed,
            Title = LocalizedText.Japanese("e"),
            Settings = new QuestionSettings
            {
                Embed = new EmbedSource(EmbedKind.Image, "https://evil.example.com/x", null, 1),
            },
        };

        var violation = DefinitionSaveRules.Check(Definition(embed), NoEmbeds());

        Assert.Equal(ServerMessageKeys.EmbedHostNotAllowed, violation?.MessageKey);
        Assert.Equal(["e1"], violation?.Fields);
    }

    [Fact]
    public void 未対応のフォールバック言語は断る()
    {
        var definition = Definition(Text("q1")) with { FallbackLanguage = "xx" };

        Assert.Equal(
            ServerMessageKeys.FallbackLanguageUnsupported,
            DefinitionSaveRules.Check(definition, NoEmbeds())?.MessageKey);
    }

    [Fact]
    public void 問題の無い定義は保存の関所を通る()
    {
        Assert.Null(DefinitionSaveRules.Check(Definition(Text("q1")), NoEmbeds()));
    }
}
