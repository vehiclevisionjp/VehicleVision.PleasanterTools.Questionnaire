using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>設問の設定そのものの不備（Issue #101）。</summary>
public enum SettingsProblemCode
{
    /// <summary>選べる数の下限が上限を上回っている。</summary>
    SelectionRangeReversed,

    /// <summary>選べる数の下限が、選択肢の数を上回っている。</summary>
    MinSelectionsExceedChoices,

    /// <summary>選べる数の下限・上限が 1 未満。</summary>
    SelectionRangeNotPositive,

    /// <summary>正規表現が組み立てられない、または使えない構文を含む（Issue #102）。</summary>
    PatternNotSupported,
}

/// <summary>設問の設定の不備 1 件。</summary>
public sealed record SettingsProblem(
    SettingsProblemCode Code,
    string QuestionId,
    string? Detail = null);

/// <summary>設問の設定が矛盾していないかを見る（Issue #101）。</summary>
/// <remarks>
/// **回答の検証（<see cref="AnswerValidator"/>）とは見るものが違う。**
/// あちらは届いた答えを見るが、こちらは**答えようのない設問になっていないか**を見る。
/// 「5 つある選択肢から 7 つ選べ」は、回答者が何をしても通らない。
/// **公開してからでは回答者にしか見えない**ので、公開の前に止める。
/// </remarks>
public static class QuestionSettingsValidator
{
    /// <summary>アンケート全体の設問設定を見る。</summary>
    public static ImmutableArray<SettingsProblem> Validate(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = ImmutableArray.CreateBuilder<SettingsProblem>();

        // **組み立てられない正規表現は、何を書いても通らない検証になる**（Issue #102）。
        // 使えない構文（先読み・後方参照・原子グループ）もここで落ちる
        foreach (var question in definition.AllQuestions
            .Where(question => !string.IsNullOrEmpty(question.Settings.Pattern))
            .Where(question => !TextPattern.IsSupported(question.Settings.Pattern)))
        {
            problems.Add(new SettingsProblem(
                SettingsProblemCode.PatternNotSupported, question.QuestionId));
        }

        foreach (var question in definition.AllQuestions.Where(question => question.HasSelectionRange))
        {
            var minimum = question.Settings.MinSelections;
            var maximum = question.Settings.MaxSelections;

            if (minimum is < 1 || maximum is < 1)
            {
                problems.Add(new SettingsProblem(
                    SettingsProblemCode.SelectionRangeNotPositive, question.QuestionId));
                continue;
            }

            if (minimum is { } min && maximum is { } max && min > max)
            {
                problems.Add(new SettingsProblem(
                    SettingsProblemCode.SelectionRangeReversed, question.QuestionId));
                continue;
            }

            // **上限が選択肢の数を超えていても害は無い**（届く数がそこまで増えない）。
            // 止めるのは下限の方だけ
            var choices = question.Choices.IsDefaultOrEmpty ? 0 : question.Choices.Length;
            if (minimum is { } required && required > choices)
            {
                problems.Add(new SettingsProblem(
                    SettingsProblemCode.MinSelectionsExceedChoices,
                    question.QuestionId,
                    choices.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return problems.ToImmutable();
    }
}
