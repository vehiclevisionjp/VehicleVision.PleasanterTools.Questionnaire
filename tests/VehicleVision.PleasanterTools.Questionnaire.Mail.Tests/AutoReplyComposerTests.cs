using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Mail.Tests;

/// <summary>回答から自動返信を組み立てる（Issue #189）。</summary>
public class AutoReplyComposerTests
{
    private static Question Email() => new()
    {
        QuestionId = "mail",
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese("メールアドレス"),
        Settings = new QuestionSettings { Format = TextFormat.Email },
    };

    private static Question Opinion() => new()
    {
        QuestionId = "opinion",
        Type = QuestionType.Paragraph,
        Title = LocalizedText.Japanese("ご意見"),
    };

    private static Question Note() => new()
    {
        QuestionId = "note",
        Type = QuestionType.Note,
        Title = LocalizedText.Japanese("はじめにお読みください"),
    };

    private static SurveyDefinition Definition(AutoReplySettings? autoReply) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages = [new Page { PageId = "p1", Questions = [Note(), Email(), Opinion()] }],
        AutoReply = autoReply,
    };

    private static AutoReplySettings Enabled => new()
    {
        Enabled = true,
        ToQuestionId = "mail",
        Subject = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "ご回答ありがとうございました",
            ["en"] = "Thank you for your response",
        }),
        Body = new LocalizedText(new Dictionary<string, string>
        {
            ["ja"] = "受け付けました。",
            ["en"] = "We received your response.",
        }),
    };

    private static ResponsePayload Payload(params PayloadAnswer[] answers) =>
        new("tok-1", [.. answers]);

    private static PayloadAnswer Answer(string questionId, params string[] values) =>
        new(questionId, [.. values]);

    private static OutgoingMail? Compose(
        AutoReplySettings? settings, ResponsePayload payload, string? language = "ja") =>
        AutoReplyComposer.Compose(Definition(settings), payload, language);

    [Fact]
    public void 設定していなければ送らない()
    {
        Assert.Null(Compose(null, Payload(Answer("mail", "a@example.test"))));
    }

    [Fact]
    public void 無効なら送らない()
    {
        var settings = Enabled with { Enabled = false };

        Assert.Null(Compose(settings, Payload(Answer("mail", "a@example.test"))));
    }

    [Fact]
    public void 宛先の設問に答えていなければ送らない()
    {
        // **任意の設問を宛先にできる以上、普通に起きる。** 異常ではない
        Assert.Null(Compose(Enabled, Payload(Answer("opinion", "よかった"))));
    }

    [Fact]
    public void 宛先が空文字なら送らない()
    {
        Assert.Null(Compose(Enabled, Payload(Answer("mail", "   "))));
    }

    [Fact]
    public void 宛先と件名と本文を組み立てる()
    {
        var mail = Compose(Enabled, Payload(Answer("mail", "a@example.test")));

        Assert.NotNull(mail);
        Assert.Equal("a@example.test", mail.ToAddress);
        Assert.Equal("ご回答ありがとうございました", mail.Subject);
        Assert.Equal("受け付けました。", mail.Body);
    }

    [Fact]
    public void 回答者の言語で組み立てる()
    {
        var mail = Compose(Enabled, Payload(Answer("mail", "a@example.test")), "en");

        Assert.NotNull(mail);
        Assert.Equal("Thank you for your response", mail.Subject);
        Assert.Equal("We received your response.", mail.Body);
    }

    [Fact]
    public void 知らない言語は既定の言語へ落とす()
    {
        var mail = Compose(Enabled, Payload(Answer("mail", "a@example.test")), "fr");

        Assert.NotNull(mail);
        Assert.Equal("ご回答ありがとうございました", mail.Subject);
    }

    [Fact]
    public void 宛先の前後の空白を落とす()
    {
        var mail = Compose(Enabled, Payload(Answer("mail", "  a@example.test  ")));

        Assert.NotNull(mail);
        Assert.Equal("a@example.test", mail.ToAddress);
    }

    [Fact]
    public void 回答キーワードを書かなければ本文へ足さない()
    {
        var mail = Compose(
            Enabled, Payload(Answer("mail", "a@example.test"), Answer("opinion", "よかった")));

        Assert.NotNull(mail);
        Assert.DoesNotContain("よかった", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void 回答キーワードの位置へ回答を並べる()
    {
        var mail = Compose(
            Enabled with { Body = LocalizedText.Japanese("回答:\n{{answers}}\n以上") },
            Payload(Answer("mail", "a@example.test"), Answer("opinion", "よかった")));

        Assert.NotNull(mail);
        Assert.Equal(
            "回答:\nメールアドレス: a@example.test" + Environment.NewLine + "ご意見: よかった\n以上",
            mail.Body);
    }

    [Fact]
    public void 写しに説明文ブロックは出さない()
    {
        // **説明文や埋め込みは回答ではない**
        var mail = Compose(
            Enabled with { Body = LocalizedText.Japanese("{{answers}}") },
            Payload(Answer("mail", "a@example.test"), Answer("note", "なにか")));

        Assert.NotNull(mail);
        Assert.DoesNotContain("はじめにお読みください", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void 写しの中の改行は潰す()
    {
        // **段落の回答がそのまま入ると、写しの行と行の境が分からなくなる**
        var mail = Compose(
            Enabled with { Body = LocalizedText.Japanese("{{answers}}") },
            Payload(Answer("mail", "a@example.test"), Answer("opinion", "1 行目\n2 行目")));

        Assert.NotNull(mail);
        Assert.Contains("ご意見: 1 行目 2 行目", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void 写しに添付は名前だけ出す()
    {
        // ⚠️ **中身（Base64）を載せると、送れない大きさのメールになる**
        var answer = new PayloadAnswer(
            "opinion", [], FileNames: ImmutableArray.Create("見積書.pdf"));

        var mail = Compose(
            Enabled with { Body = LocalizedText.Japanese("{{answers}}") },
            Payload(Answer("mail", "a@example.test"), answer));

        Assert.NotNull(mail);
        Assert.Contains("見積書.pdf", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void 件名と本文に題名を差し込む()
    {
        // **複製しても題名が古いまま残らない**（Issue #209）
        var settings = Enabled with
        {
            Subject = LocalizedText.Japanese("{{title}} へのご回答"),
            Body = LocalizedText.Japanese("{{title}} にご協力ありがとうございました。"),
        };

        var mail = Compose(settings, Payload(Answer("mail", "a@example.test")));

        Assert.NotNull(mail);
        Assert.Equal("検証用 へのご回答", mail.Subject);
        Assert.Equal("検証用 にご協力ありがとうございました。", mail.Body);
    }

    [Fact]
    public void 受付日時は渡したものを差し込む()
    {
        var settings = Enabled with { Body = LocalizedText.Japanese("受付: {{submittedAt}}") };
        var submittedAt = new DateTimeOffset(2026, 9, 14, 15, 4, 0, TimeSpan.FromHours(9));

        var mail = AutoReplyComposer.Compose(
            Definition(settings), Payload(Answer("mail", "a@example.test")), "ja", submittedAt);

        Assert.NotNull(mail);
        Assert.Equal("受付: 2026-09-14 15:04", mail.Body);
    }

    [Fact]
    public void 差し込みは回答者の言語の題名を使う()
    {
        var definition = new SurveyDefinition
        {
            SurveyId = "s1",
            Version = 1,
            Title = new LocalizedText(new Dictionary<string, string>
            {
                ["ja"] = "満足度調査",
                ["en"] = "Satisfaction survey",
            }),
            Pages = [new Page { PageId = "p1", Questions = [Note(), Email(), Opinion()] }],
            AutoReply = Enabled with { Subject = LocalizedText.Japanese("{{title}}") },
        };

        var mail = AutoReplyComposer.Compose(
            definition, Payload(Answer("mail", "a@example.test")), "en");

        Assert.NotNull(mail);
        Assert.Equal("Satisfaction survey", mail.Subject);
    }

    [Fact]
    public void 件名が空なら送らない()
    {
        // **公開のときに弾いている。** ここへ来るのは定義を直接書き換えた場合だけ
        var settings = Enabled with { Subject = null };

        Assert.Null(Compose(settings, Payload(Answer("mail", "a@example.test"))));
    }
}
