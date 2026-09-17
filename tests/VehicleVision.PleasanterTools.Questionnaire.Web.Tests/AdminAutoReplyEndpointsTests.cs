using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>保存前の自動返信プレビュー（Issue #319）。</summary>
public class AdminAutoReplyEndpointsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 17, 9, 34, 0, TimeSpan.Zero);
    private static readonly PleasanterOptions Pleasanter = new()
    {
        BaseUrl = "https://pleasanter.example.test",
        ApiKey = "test",
        ApiKeyUserTimeZoneId = "UTC",
    };

    private static SurveyDefinition Definition(string body) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "満足度調査",
            ["en"] = "Satisfaction survey",
        }),
        AllowEditingAfterSubmit = true,
        AutoReply = new AutoReplySettings
        {
            Enabled = true,
            ToQuestionId = "mail",
            Subject = LocalizedText.Japanese("{{title}} の受付"),
            Body = LocalizedText.Japanese(body),
            EditLinkDays = 7,
        },
        Pages =
        [
            new Page
            {
                PageId = "p1",
                Questions =
                [
                    new Question
                    {
                        QuestionId = "mail",
                        Type = QuestionType.Text,
                        Title = LocalizedText.Japanese("メール"),
                        Settings = new QuestionSettings { Format = TextFormat.Email },
                    },
                    new Question
                    {
                        QuestionId = "opinion",
                        Type = QuestionType.Paragraph,
                        Title = LocalizedText.Japanese("ご意見"),
                    },
                ],
            },
        ],
    };

    [Fact]
    public void 本物の合成結果と一致する()
    {
        var definition = Definition(
            "{{submittedAt}}\n{{answers}}\n{{formUrl}}\n{{editUrl}}\n{{editUrlExpiresAt}}");
        var options = new MailOptions { BaseUrl = "https://survey.example.test" };

        var preview = AdminAutoReplyEndpoints.Preview(
            definition,
            "ja",
            options,
            Pleasanter,
            Now);
        var expected = AutoReplyComposer.Compose(
            definition,
            new ResponsePayload("preview",
            [
                new PayloadAnswer("mail", ["preview@example.invalid"]),
                new PayloadAnswer("opinion", ["見本の回答"]),
            ]),
            "ja",
            Now,
            new AutoReplyPlaceholderValues(
                FormUrl: "https://survey.example.test/f/preview",
                EditUrl: "https://survey.example.test/f/preview#e=preview",
                EditUrlExpiresAt: Now.AddDays(7)));

        Assert.NotNull(expected);
        Assert.Equal(expected.Subject, preview.Subject);
        Assert.Equal(expected.Body, preview.Body);
    }

    [Fact]
    public void 未知のキーワードを警告へ挙げて本文には残す()
    {
        var preview = AdminAutoReplyEndpoints.Preview(
            Definition("{{titel}} / {{unknown}} / {{titel}}"),
            "ja",
            new MailOptions(),
            Pleasanter,
            Now);

        Assert.Equal<string>(["titel", "unknown"], preview.UnknownKeywords);
        Assert.Equal("{{titel}} / {{unknown}} / {{titel}}", preview.Body);
    }

    [Fact]
    public void 言語を切り替えるとその言語の文面になる()
    {
        var definition = Definition("日本語") with
        {
            AutoReply = Definition("日本語").AutoReply! with
            {
                Subject = new LocalizedText(new Dictionary<string, string>
                {
                    ["ja"] = "{{title}} の受付",
                    ["en"] = "{{title}} received",
                }),
                Body = new LocalizedText(new Dictionary<string, string>
                {
                    ["ja"] = "日本語",
                    ["en"] = "English",
                }),
            },
        };

        var preview = AdminAutoReplyEndpoints.Preview(
            definition,
            "en",
            new MailOptions(),
            Pleasanter,
            Now);

        Assert.Equal("Satisfaction survey received", preview.Subject);
        Assert.Equal("English", preview.Body);
    }

    [Fact]
    public void メールで配る資産があればリンクをプレビューする()
    {
        var definition = Definition("{{assetsUrl}}\n{{assetsUrlExpiresAt}}") with
        {
            ConfirmationMessage = LocalizedText.Japanese(
                "[資料](asset:11111111-1111-1111-1111-111111111111)"),
            AssetDelivery = new AssetDeliverySettings
            {
                Expiration = AssetTicketExpiration.DaysAfterResponse,
                Days = 3,
            },
        };

        var preview = AdminAutoReplyEndpoints.Preview(
            definition,
            "ja",
            new MailOptions { BaseUrl = "https://survey.example.test" },
            Pleasanter,
            Now);

        Assert.Equal(
            "https://survey.example.test/f/preview#d=preview\n2026-09-20 09:34",
            preview.Body);
    }
}
