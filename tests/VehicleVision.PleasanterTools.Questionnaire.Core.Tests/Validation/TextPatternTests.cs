using System.Collections.Immutable;
using System.Diagnostics;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Validation;

/// <summary>正規表現による入力検証（Issue #102）。</summary>
public class TextPatternTests
{
    private static SurveyDefinition Definition(QuestionSettings settings) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("検証用"),
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
                        Title = LocalizedText.Japanese("社員番号"),
                        Settings = settings,
                    },
                ],
            },
        ],
    };

    private static ValidationErrorCode[] Codes(ImmutableArray<ValidationError> errors) =>
        [.. errors.Select(error => error.Code)];

    [Fact]
    public void 合っていれば通す()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9]{4}" });

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "1234")]));
    }

    [Fact]
    public void 合わなければ拒否する()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9]{4}" });

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "12a4")]);

        Assert.Equal([ValidationErrorCode.PatternMismatch], Codes(errors));
    }

    /// <remarks>
    /// **部分一致にしない。**「数字 4 桁」のつもりの指定が
    /// 「どこかに数字 4 桁があればよい」になってしまう。
    /// </remarks>
    [Fact]
    public void 値の全体で見る()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9]{4}" });

        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "社員1234番")]);

        Assert.Equal([ValidationErrorCode.PatternMismatch], Codes(errors));
    }

    /// <remarks>**選択（`a|b`）を書かれても、囲みで意図が変わらない。**</remarks>
    [Fact]
    public void 選択を書いても全体一致のまま()
    {
        var definition = Definition(new QuestionSettings { Pattern = "はい|いいえ" });

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "はい")]));

        Assert.Equal(
            [ValidationErrorCode.PatternMismatch],
            Codes(AnswerValidator.Validate(definition, [Answer.Of("q1", "はいそうです")])));
    }

    /// <remarks>**未回答には効かない。** 任意の設問を答えずに送れなくなってしまう。</remarks>
    [Fact]
    public void 未回答には効かない()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9]{4}" });

        Assert.Empty(AnswerValidator.Validate(definition, []));
    }

    [Fact]
    public void 形式の指定と重ねて効く()
    {
        var definition = Definition(new QuestionSettings
        {
            Format = TextFormat.Email,
            Pattern = ".*@example\\.com",
        });

        Assert.Empty(AnswerValidator.Validate(definition, [Answer.Of("q1", "a@example.com")]));

        // メールとしては妥当だが、指定した形には合わない
        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", "a@example.net")]);
        Assert.Equal([ValidationErrorCode.PatternMismatch], Codes(errors));
    }

    /// <remarks>
    /// ⚠️ **これが本題。** 素の正規表現なら、この組み合わせで照合が終わらなくなる
    /// （ReDoS）。**回答は誰でも送れる**ので、入力の長さも中身も相手が選べる。
    /// </remarks>
    [Fact]
    public void 破滅的な後退戻りを起こす指定でも即座に終わる()
    {
        var definition = Definition(new QuestionSettings { Pattern = "(a+)+b" });
        var attack = new string('a', 40);

        var stopwatch = Stopwatch.StartNew();
        var errors = AnswerValidator.Validate(definition, [Answer.Of("q1", attack)]);
        stopwatch.Stop();

        Assert.Equal([ValidationErrorCode.PatternMismatch], Codes(errors));
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"照合に {stopwatch.Elapsed} かかった。後退戻りしない照合器で動いていない疑いがある");
    }

    [Fact]
    public void 組み立てられない正規表現は公開の前に止める()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9" });

        Assert.Equal(
            [SettingsProblemCode.PatternNotSupported],
            QuestionSettingsValidator.Validate(definition).Select(problem => problem.Code));
    }

    /// <remarks>
    /// **先読みは後退戻りしない照合器で使えない。** 使えるふりをして通すと、
    /// 公開してから「何を入れても通らない」設問になる。
    /// </remarks>
    [Fact]
    public void 使えない構文は公開の前に止める()
    {
        var definition = Definition(new QuestionSettings { Pattern = "(?=.*[0-9]).{8,}" });

        Assert.Equal(
            [SettingsProblemCode.PatternNotSupported],
            QuestionSettingsValidator.Validate(definition).Select(problem => problem.Code));
    }

    [Fact]
    public void 使える正規表現は止めない()
    {
        var definition = Definition(new QuestionSettings { Pattern = "[0-9]{3}-[0-9]{4}" });

        Assert.Empty(QuestionSettingsValidator.Validate(definition));
    }

    /// <remarks>**通してしまうと、壊れた指定が素通りの検証になる。**</remarks>
    [Fact]
    public void 組み立てられない正規表現は合わない扱いにする()
    {
        Assert.False(TextPattern.IsMatch("[0-9", "1234"));
        Assert.False(TextPattern.IsSupported("[0-9"));
    }
}
