using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>回答画面へ返す応答の組み立て（Issue #874）。</summary>
public sealed class FormResponseTests
{
    private static SurveyDefinition Definition(bool withLookup, bool withServerSettings = false) => new()
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
        AutoReply = withServerSettings
            ? new AutoReplySettings
            {
                Enabled = true,
                ToQuestionId = "k1",
                Subject = LocalizedText.Japanese("SUBJECT-secret"),
                Body = LocalizedText.Japanese("BODY-secret"),
                FromName = LocalizedText.Japanese("FROM-secret"),
                ReplyToAddress = "reply-secret@example.com",
                BccAddress = "bcc-secret@example.com",
                EditLinkDays = 11,
            }
            : null,
        AssetDelivery = withServerSettings
            ? new AssetDeliverySettings { Expiration = AssetTicketExpiration.DaysAfterResponse, Days = 77 }
            : null,
        ConfirmationMessage = LocalizedText.Japanese("完了"),
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

    [Fact]
    public void 自動返信と資産の引換券の設定を_definitionから除く()
    {
        var response = FormResponse.Create("pub", Definition(withLookup: false, withServerSettings: true), requiresProofOfWork: false);

        Assert.Null(response.Definition.AutoReply);
        Assert.Null(response.Definition.AssetDelivery);

        var text = Serialize(response);
        foreach (var leaked in new[]
        {
            "autoReply", "bccAddress", "replyToAddress", "toQuestionId", "editLinkDays", "fromName",
            "assetDelivery", "expiration", "bcc-secret", "reply-secret", "SUBJECT-secret", "BODY-secret", "FROM-secret",
        })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }

        // 回答画面が使う項目は残す
        using var json = JsonDocument.Parse(text);
        var definition = json.RootElement.GetProperty("definition");
        Assert.Equal("題名", definition.GetProperty("title").GetProperty("ja").GetString());
        Assert.Equal("完了", definition.GetProperty("confirmationMessage").GetProperty("ja").GetString());
        Assert.Equal(2, definition.GetProperty("pages")[0].GetProperty("questions").GetArrayLength());
        Assert.True(definition.TryGetProperty("allowEditingAfterSubmit", out _));
        Assert.True(definition.TryGetProperty("showProgress", out _));
    }

    [Fact]
    public void 公開版の定義そのものは変えない()
    {
        var source = Definition(withLookup: true, withServerSettings: true);

        _ = FormResponse.Create("pub", source, requiresProofOfWork: false);

        Assert.NotNull(source.AutoReply);
        Assert.NotNull(source.AssetDelivery);
        Assert.NotNull(source.Lookups);
    }
}
