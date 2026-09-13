using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>自動返信の設定（Issue #189）。**公開の前に止める。**</summary>
public class AutoReplyValidatorTests
{
    private static Question Email(string questionId = "mail") => new()
    {
        QuestionId = questionId,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese("メールアドレス"),
        Settings = new QuestionSettings { Format = TextFormat.Email },
    };

    private static Question FreeText(string questionId = "free") => new()
    {
        QuestionId = questionId,
        Type = QuestionType.Text,
        Title = LocalizedText.Japanese("ご意見"),
    };

    private static SurveyDefinition Definition(
        AutoReplySettings? autoReply, params Question[] questions) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
        Pages = [new Page { PageId = "p1", Questions = [.. questions] }],
        AutoReply = autoReply,
    };

    private static AutoReplySettings Valid => new()
    {
        Enabled = true,
        ToQuestionId = "mail",
        Subject = LocalizedText.Japanese("ご回答ありがとうございました"),
        Body = LocalizedText.Japanese("受け付けました。"),
    };

    private static AutoReplyProblemCode[] Codes(ImmutableArray<AutoReplyProblem> problems) =>
        [.. problems.Select(problem => problem.Code)];

    [Fact]
    public void 設定していなければ何も言わない()
    {
        Assert.Empty(AutoReplyValidator.Validate(Definition(null, Email())));
    }

    [Fact]
    public void 無効なら中身を見ない()
    {
        // **使わない設定の不備で公開を止めない**
        var settings = new AutoReplySettings { Enabled = false };

        Assert.Empty(AutoReplyValidator.Validate(Definition(settings, Email())));
    }

    [Fact]
    public void 揃っていれば通る()
    {
        Assert.Empty(AutoReplyValidator.Validate(Definition(Valid, Email())));
    }

    [Fact]
    public void 宛先の設問が未指定なら止める()
    {
        var settings = Valid with { ToQuestionId = null };

        Assert.Equal(
            [AutoReplyProblemCode.ToQuestionMissing],
            Codes(AutoReplyValidator.Validate(Definition(settings, Email()))));
    }

    [Fact]
    public void 消した設問を指していたら止める()
    {
        // **設問を消したあと、指しっぱなしになる**
        Assert.Equal(
            [AutoReplyProblemCode.ToQuestionNotFound],
            Codes(AutoReplyValidator.Validate(Definition(Valid, FreeText()))));
    }

    [Fact]
    public void メール形式でない設問は宛先にできない()
    {
        // **何を書いても通る欄を宛先にすると、送信は必ず失敗してデッドレターが溜まる**
        var settings = Valid with { ToQuestionId = "free" };

        Assert.Equal(
            [AutoReplyProblemCode.ToQuestionNotEmail],
            Codes(AutoReplyValidator.Validate(Definition(settings, Email(), FreeText()))));
    }

    [Fact]
    public void 件名が空なら止める()
    {
        var settings = Valid with { Subject = LocalizedText.Japanese("   ") };

        Assert.Equal(
            [AutoReplyProblemCode.SubjectMissing],
            Codes(AutoReplyValidator.Validate(Definition(settings, Email()))));
    }

    [Fact]
    public void 本文が空なら止める()
    {
        var settings = Valid with { Body = null };

        Assert.Equal(
            [AutoReplyProblemCode.BodyMissing],
            Codes(AutoReplyValidator.Validate(Definition(settings, Email()))));
    }

    [Fact]
    public void 英語だけ書いてあっても不備にしない()
    {
        // **翻訳漏れであって不備ではない**（既定の言語へ落ちる）
        var settings = Valid with
        {
            Subject = new LocalizedText(new Dictionary<string, string> { ["en"] = "Thank you" }),
            Body = new LocalizedText(new Dictionary<string, string> { ["en"] = "Received." }),
        };

        Assert.Empty(AutoReplyValidator.Validate(Definition(settings, Email())));
    }

    [Fact]
    public void 不備は溜めて返す()
    {
        // **1 つ直しては公開し直す、を繰り返させない**
        var settings = new AutoReplySettings { Enabled = true };

        Assert.Equal(
            [
                AutoReplyProblemCode.ToQuestionMissing,
                AutoReplyProblemCode.SubjectMissing,
                AutoReplyProblemCode.BodyMissing,
            ],
            Codes(AutoReplyValidator.Validate(Definition(settings, Email()))));
    }

    [Fact]
    public void 不備の補足に宛先そのものを入れない()
    {
        // ⚠️ **補足は画面に出る。** 設問の識別子までにとどめる
        var problems = AutoReplyValidator.Validate(Definition(Valid, FreeText()));

        Assert.Equal("mail", problems.Single().Detail);
    }
}
