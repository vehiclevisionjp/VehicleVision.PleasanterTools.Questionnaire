using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Flow;

/// <summary>設問と釦で共有する回答条件の評価器。</summary>
public static class ConditionEvaluator
{
    /// <summary>条件を満たすか。未指定または空なら満たす。</summary>
    /// <remarks>
    /// **出していない設問の答えは見ない。** 通らなかったページや隠した設問の答えが
    /// 残っていても、回答者が見た状態だけで判定する。
    /// </remarks>
    public static bool IsSatisfied(
        VisibilityCondition? condition,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visibleQuestionIds)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(visibleQuestionIds);

        if (condition is null || condition.IsEmpty)
        {
            return true;
        }

        var results = condition.Rules.Select(
            rule => Matches(rule, answers, visibleQuestionIds));

        return condition.Match is ConditionMatch.Any
            ? results.Any(matched => matched)
            : results.All(matched => matched);
    }

    private static bool Matches(
        ConditionRule rule,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visibleQuestionIds)
    {
        var answered = visibleQuestionIds.Contains(rule.QuestionId)
            && answers.TryGetValue(rule.QuestionId, out var found)
            && !found.IsEmpty;

        var answer = answered ? answers[rule.QuestionId] : null;

        return rule.Operator switch
        {
            ConditionOperator.Answered => answered,
            ConditionOperator.NotAnswered => !answered,
            ConditionOperator.Equals =>
                answer is not null && answer.Values.Any(
                    value => string.Equals(value, rule.Value, StringComparison.Ordinal)),
            ConditionOperator.NotEquals =>
                answer is not null && !answer.Values.Any(
                    value => string.Equals(value, rule.Value, StringComparison.Ordinal)),
            ConditionOperator.Contains =>
                answer is not null && rule.Value is not null && answer.Values.Any(
                    value => value.Contains(rule.Value, StringComparison.Ordinal)),
            ConditionOperator.GreaterThan => Compare(answer, rule.Value) > 0,
            ConditionOperator.LessThan => Compare(answer, rule.Value) < 0,
            _ => false,
        };
    }

    private static int Compare(Answer? answer, string? value)
    {
        if (answer?.SingleValue is not { } left
            || !decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftNumber)
            || !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightNumber))
        {
            return 0;
        }

        return leftNumber.CompareTo(rightNumber);
    }
}
