using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理者のパスワードの条件（Issue #157）。</summary>
/// <remarks>
/// **設定で変えられるようになったので、既定と設定した場合の両方を見る。**
/// </remarks>
public class AdminPasswordPolicyTests
{
    private static AdminPasswordPolicy Create(AdminPasswordPolicyOptions? options = null) =>
        new(options ?? new AdminPasswordPolicyOptions());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("           ")]
    public void 無い入力や短い入力は受け付けない(string? password)
    {
        Assert.False(Create().IsAcceptable(password));
    }

    [Fact]
    public void 最低長の一文字手前は受け付けない()
    {
        var policy = Create();

        Assert.False(policy.IsAcceptable(new string('a', policy.MinimumLength - 1)));
    }

    [Fact]
    public void 最低長ちょうどなら受け付ける()
    {
        var policy = Create();

        Assert.True(policy.IsAcceptable(new string('a', policy.MinimumLength)));
    }

    [Fact]
    public void 最低長は設定で変えられる()
    {
        var policy = Create(new AdminPasswordPolicyOptions { MinimumLength = 20 });

        Assert.False(policy.IsAcceptable(new string('a', 19)));
        Assert.True(policy.IsAcceptable(new string('a', 20)));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void 短すぎる文言には最低長が入る(string language)
    {
        var policy = Create();
        var message = policy.Message(language);

        Assert.Contains(policy.MinimumLength.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 対応していない言語は既定の文言へ落ちる()
    {
        var policy = Create();

        Assert.Equal(policy.Message(SupportedLanguages.Default), policy.Message("fr"));
    }

    // ---- ログイン ID との一致 -----------------------------------------------

    [Fact]
    public void 既定ではログインIDと同じパスワードを断る()
    {
        var policy = Create();
        var loginId = "kanri-tantou-admin";

        Assert.False(policy.IsAcceptable(loginId, loginId));

        // **大文字小文字を変えただけでも同じ扱い**
        Assert.False(policy.IsAcceptable(loginId.ToUpperInvariant(), loginId));
    }

    [Fact]
    public void 許す設定ならログインIDと同じでも通る()
    {
        var policy = Create(new AdminPasswordPolicyOptions { AllowSameAsLoginId = true });
        var loginId = "kanri-tantou-admin";

        Assert.True(policy.IsAcceptable(loginId, loginId));
    }

    [Fact]
    public void ログインIDが分からなければその条件は見ない()
    {
        var policy = Create();

        // **招待の受け取りのように、先に ID が分からない場面がある**
        Assert.True(policy.IsAcceptable("long-enough-password", loginId: null));
    }

    // ---- 正規表現の条件（Pleasanter と同じ形）--------------------------------

    private static AdminPasswordPolicyOptions WithRule(
        string regex,
        string? ja = null,
        string? fallback = null,
        bool enabled = true)
    {
        var messages = new List<AdminPasswordRuleMessage>();
        if (fallback is not null)
        {
            messages.Add(new AdminPasswordRuleMessage { Body = fallback });
        }

        if (ja is not null)
        {
            messages.Add(new AdminPasswordRuleMessage { Language = "ja", Body = ja });
        }

        return new AdminPasswordPolicyOptions
        {
            MinimumLength = 8,
            Policies = [new AdminPasswordRule { Enabled = enabled, Regex = regex, Languages = messages }],
        };
    }

    [Fact]
    public void 正規表現は部分一致で見る()
    {
        // **本体と同じ**（RegexExists）。「どこかに数字がある」を意味する
        var policy = Create(WithRule("[0-9]"));

        Assert.True(policy.IsAcceptable("password1"));
        Assert.False(policy.IsAcceptable("passwordonly"));
    }

    [Fact]
    public void 全体一致にしたいときは前後を固定して書く()
    {
        var policy = Create(WithRule("^[a-z]+$"));

        Assert.True(policy.IsAcceptable("onlylowercase"));
        Assert.False(policy.IsAcceptable("has1digit"));
    }

    [Fact]
    public void 無効にした条件は見ない()
    {
        var policy = Create(WithRule("[0-9]", enabled: false));

        Assert.True(policy.IsAcceptable("passwordonly"));
    }

    [Fact]
    public void 条件の文言は言語に合わせて返す()
    {
        var policy = Create(WithRule("[0-9]", ja: "数字を 1 つ以上入れてください。", fallback: "Include a digit."));

        Assert.Equal("数字を 1 つ以上入れてください。", policy.Check("passwordonly", null, "ja"));
        Assert.Equal("Include a digit.", policy.Check("passwordonly", null, "en"));
    }

    [Fact]
    public void 言語の指定が無い文言は既定として使う()
    {
        var policy = Create(WithRule("[0-9]", fallback: "Include a digit."));

        Assert.Equal("Include a digit.", policy.Check("passwordonly", null, "ja"));
    }

    [Fact]
    public void 文言を書き忘れても通してしまわない()
    {
        var policy = Create(WithRule("[0-9]"));

        var message = policy.Check("passwordonly", null, "ja");

        Assert.NotNull(message);
        Assert.Equal(ServerMessages.Get(ServerMessageKeys.PasswordPolicyMismatch, "ja"), message);
    }

    [Fact]
    public void 先に見た条件の文言を返す()
    {
        var policy = Create(new AdminPasswordPolicyOptions
        {
            MinimumLength = 8,
            Policies =
            [
                new AdminPasswordRule
                {
                    Regex = "[0-9]",
                    Languages = [new AdminPasswordRuleMessage { Body = "digit" }],
                },
                new AdminPasswordRule
                {
                    Regex = "[A-Z]",
                    Languages = [new AdminPasswordRuleMessage { Body = "upper" }],
                },
            ],
        });

        // **どちらも満たしていないが、先に書いた条件の文言だけを返す**
        Assert.Equal("digit", policy.Check("lowercaseonly", null, "en"));
    }

    // ---- 危ない正規表現は作らせない ------------------------------------------

    [Theory]
    [InlineData("(?=.*[0-9])")]
    [InlineData("(a)\\1")]
    [InlineData("(?>a+)b")]
    public void 後退戻りが要る書き方は作る時点で落とす(string regex)
    {
        // **黙って無効にしない。** 条件が無いまま動くのが一番危ない
        var exception = Assert.Throws<InvalidOperationException>(() => Create(WithRule(regex)));

        Assert.Contains(regex, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 組み立てられない正規表現も作る時点で落とす()
    {
        Assert.Throws<InvalidOperationException>(() => Create(WithRule("[0-9")));
    }

    [Fact]
    public void 空の正規表現は設定の誤りとして落とす()
    {
        Assert.Throws<InvalidOperationException>(() => Create(WithRule(string.Empty)));
    }

    [Fact]
    public void 長さの条件は正規表現より先に見る()
    {
        var policy = Create(new AdminPasswordPolicyOptions
        {
            MinimumLength = 12,
            Policies =
            [
                new AdminPasswordRule
                {
                    Regex = "[0-9]",
                    Languages = [new AdminPasswordRuleMessage { Body = "digit" }],
                },
            ],
        });

        // 短くて数字も無い。**長さの文言が返る**
        var message = policy.Check("ab1", null, "ja");

        Assert.NotNull(message);
        Assert.DoesNotContain("digit", message!, StringComparison.Ordinal);
    }
}
