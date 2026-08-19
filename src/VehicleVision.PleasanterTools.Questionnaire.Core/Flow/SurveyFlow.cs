using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Flow;

/// <summary>回答に応じて、実際に辿るページと出す設問を決める。</summary>
/// <remarks>
/// <para>
/// **ジャンプ方式**（Issue #41）。選択肢ごとの行き先が優先され、
/// 無ければページ末尾の行き先、それも無ければ次のページへ進む。
/// </para>
/// <para>
/// **サーバ側で必ず通すこと。** 画面が辿った経路をそのまま信じてはいけない。
/// 信じると、画面を通さずに送るだけで**隠したページの設問へ書き込める**。
/// </para>
/// <para>
/// **止まらないことを保証する。** ジャンプ先が前を向いていないことは公開時に
/// 検査する（<see cref="SurveyFlowValidator"/>）が、
/// **万一そうなっていても、ここは既に通ったページへは戻らない**。
/// </para>
/// </remarks>
public static class SurveyFlow
{
    /// <summary>辿った経路。</summary>
    /// <param name="Pages">通ったページ。**順番どおり。**</param>
    /// <param name="VisibleQuestionIds">出した設問。</param>
    public sealed record Path(
        ImmutableArray<Page> Pages,
        ImmutableHashSet<string> VisibleQuestionIds)
    {
        /// <summary>そのページを通ったか。</summary>
        public bool Visited(string pageId) =>
            Pages.Any(page => string.Equals(page.PageId, pageId, StringComparison.Ordinal));

        /// <summary>その設問を出したか。</summary>
        public bool Visible(string questionId) => VisibleQuestionIds.Contains(questionId);
    }

    /// <summary>回答から経路を求める。</summary>
    public static Path Trace(SurveyDefinition definition, IReadOnlyCollection<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(answers);

        var answerByQuestion = answers
            .GroupBy(answer => answer.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var byId = definition.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var order = definition.Pages
            .Select((page, index) => (page.PageId, index))
            .ToDictionary(entry => entry.PageId, entry => entry.index, StringComparer.Ordinal);

        var pages = ImmutableArray.CreateBuilder<Page>();
        var visible = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        // **同じページを 2 度通らない。** 検査を抜けた定義でも、ここで止まる
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var current = definition.Pages.IsDefaultOrEmpty ? null : definition.Pages[0];

        while (current is not null && seen.Add(current.PageId))
        {
            pages.Add(current);

            // **出す設問はその場で決まる。** 前の設問の答えしか見ないため
            foreach (var question in current.Questions)
            {
                if (IsVisible(question, answerByQuestion, visible))
                {
                    visible.Add(question.QuestionId);
                }
            }

            current = NextPage(current, definition, byId, order, answerByQuestion, visible);
        }

        return new Path(pages.ToImmutable(), visible.ToImmutable());
    }

    /// <summary>その設問を出すか。</summary>
    /// <remarks>
    /// **出していない設問の答えは見ない。** 見ると、隠れている設問の答えで
    /// 別の設問が出る、という追えない挙動になる。
    /// </remarks>
    private static bool IsVisible(
        Question question,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visibleSoFar)
    {
        if (question.VisibleWhen is not { } condition || condition.IsEmpty)
        {
            return true;
        }

        var results = condition.Rules.Select(rule => Matches(rule, answers, visibleSoFar));

        return condition.Match is ConditionMatch.Any
            ? results.Any(matched => matched)
            : results.All(matched => matched);
    }

    private static bool Matches(
        ConditionRule rule,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visibleSoFar)
    {
        // **通らなかったページ・出していない設問は「未回答」。**
        // 答えが残っていても、見せていない以上は無かったことにする
        var answered = visibleSoFar.Contains(rule.QuestionId)
            && answers.TryGetValue(rule.QuestionId, out var found)
            && !found.IsEmpty;

        var answer = answered ? answers[rule.QuestionId] : null;

        return rule.Operator switch
        {
            ConditionOperator.Answered => answered,
            ConditionOperator.NotAnswered => !answered,

            ConditionOperator.Equals =>
                answer is not null && answer.Values.Any(value =>
                    string.Equals(value, rule.Value, StringComparison.Ordinal)),

            // **未回答は「等しくない」に含めない。** 含めると、
            // まだ答えていないだけの設問で条件が成立してしまう
            ConditionOperator.NotEquals =>
                answer is not null && !answer.Values.Any(value =>
                    string.Equals(value, rule.Value, StringComparison.Ordinal)),

            ConditionOperator.Contains =>
                answer is not null && rule.Value is not null && answer.Values.Any(value =>
                    value.Contains(rule.Value, StringComparison.Ordinal)),

            ConditionOperator.GreaterThan => Compare(answer, rule.Value) > 0,
            ConditionOperator.LessThan => Compare(answer, rule.Value) < 0,

            _ => false,
        };
    }

    /// <summary>数として比べる。**数で読めなければ成立しない。**</summary>
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

    /// <summary>次に進むページ。**無ければ送信へ。**</summary>
    private static Page? NextPage(
        Page current,
        SurveyDefinition definition,
        IReadOnlyDictionary<string, Page> byId,
        IReadOnlyDictionary<string, int> order,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visible)
    {
        var transition = ChoiceTransition(current, answers, visible) ?? current.Next;

        switch (transition?.Kind)
        {
            case PageTransitionKind.Submit:
                return null;

            case PageTransitionKind.Page when transition.PageId is { } pageId:
                return byId.GetValueOrDefault(pageId);

            default:
                // **既定は次のページ。** 最後まで来たら送信へ
                var index = order[current.PageId] + 1;
                return index < definition.Pages.Length ? definition.Pages[index] : null;
        }
    }

    /// <summary>選んだ選択肢が持つ行き先。</summary>
    /// <remarks>
    /// **1 ページに行き先を持つ設問は 1 つだけ**（<see cref="SurveyFlowValidator"/> が弾く）。
    /// 検査を抜けた定義では、**先に出てくるものを使う**。
    /// </remarks>
    private static PageTransition? ChoiceTransition(
        Page page,
        IReadOnlyDictionary<string, Answer> answers,
        IReadOnlyCollection<string> visible)
    {
        foreach (var question in page.Questions.Where(question => question.HasChoiceTransitions))
        {
            // **出していない設問の選択肢では飛ばさない**
            if (!visible.Contains(question.QuestionId)
                || !answers.TryGetValue(question.QuestionId, out var answer)
                || answer.SingleValue is not { } value)
            {
                continue;
            }

            var choice = question.Choices.FirstOrDefault(
                candidate => string.Equals(candidate.Value, value, StringComparison.Ordinal));

            if (choice?.Next is { } transition)
            {
                return transition;
            }
        }

        return null;
    }
}
