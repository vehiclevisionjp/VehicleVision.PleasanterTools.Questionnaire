using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Flow;

/// <summary>分岐の不備。</summary>
public enum FlowProblemCode
{
    /// <summary>飛び先のページが存在しない。</summary>
    UnknownPage,

    /// <summary>飛び先が前を向いている。**無限に回るアンケートを作れてしまう。**</summary>
    BackwardTransition,

    /// <summary>自分自身へ飛ぼうとしている。</summary>
    SelfTransition,

    /// <summary>どう答えても辿り着けないページがある。</summary>
    UnreachablePage,

    /// <summary>1 ページに、行き先を持つ設問が 2 つ以上ある。</summary>
    MultipleBranchingQuestions,

    /// <summary>単一選択でない設問に行き先が付いている。</summary>
    TransitionOnUnsupportedQuestion,

    /// <summary>表示条件の参照先が存在しない。</summary>
    UnknownConditionQuestion,

    /// <summary>表示条件が自分より後ろ（または自分自身）を参照している。</summary>
    ForwardConditionReference,

    /// <summary>表示条件が、その設問に無い選択肢の値を見ている。**永久に成立しない。**</summary>
    UnknownChoiceValue,
}

/// <summary>分岐の不備 1 件。</summary>
/// <param name="Code">何が問題か。</param>
/// <param name="PageId">どのページか。</param>
/// <param name="QuestionId">どの設問か。ページ末尾の行き先では <c>null</c>。</param>
/// <param name="Detail">補足（飛び先のページ ID など）。</param>
public sealed record FlowProblem(
    FlowProblemCode Code,
    string? PageId = null,
    string? QuestionId = null,
    string? Detail = null);

/// <summary>公開する前に、分岐が壊れていないかを見る。</summary>
/// <remarks>
/// <para>
/// **ジャンプ方式は壊れた形を作れてしまう**（Issue #41）。
/// 無限に回る・辿り着けないページができる、といった不備は
/// **公開してからでは回答者にしか見えない**ので、公開の前に弾く。
/// </para>
/// <para>
/// **前を向いた行き先は禁止。** 「戻る」を許すと無限に回るアンケートが作れる。
/// 検査で弾くことで、<see cref="SurveyFlow"/> が必ず止まることを担保する。
/// </para>
/// </remarks>
public static class SurveyFlowValidator
{
    /// <summary>不備を挙げる。**空なら公開してよい。**</summary>
    public static ImmutableArray<FlowProblem> Validate(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = ImmutableArray.CreateBuilder<FlowProblem>();

        var order = definition.Pages
            .Select((page, index) => (page.PageId, index))
            .ToDictionary(entry => entry.PageId, entry => entry.index, StringComparer.Ordinal);

        ValidateTransitions(definition, order, problems);
        ValidateConditions(definition, problems);
        ValidateReachability(definition, problems);

        return problems.ToImmutable();
    }

    // ---- 行き先 -------------------------------------------------------------

    private static void ValidateTransitions(
        SurveyDefinition definition,
        IReadOnlyDictionary<string, int> order,
        ImmutableArray<FlowProblem>.Builder problems)
    {
        foreach (var page in definition.Pages)
        {
            CheckTransition(page.Next, order, page.PageId, questionId: null, problems);

            var branching = page.Questions.Where(question => question.HasChoiceTransitions).ToList();

            // **どれが勝つのかを利用者が決められない形にしない**
            if (branching.Count > 1)
            {
                problems.Add(new FlowProblem(
                    FlowProblemCode.MultipleBranchingQuestions,
                    page.PageId,
                    Detail: string.Join(
                        " / ", branching.Select(question => question.QuestionId))));
            }

            foreach (var question in branching)
            {
                if (!question.CanCarryTransitions)
                {
                    // **複数選べる設問では、どの選択肢の行き先を使うのか決まらない**
                    problems.Add(new FlowProblem(
                        FlowProblemCode.TransitionOnUnsupportedQuestion,
                        page.PageId,
                        question.QuestionId,
                        question.Type.ToString()));
                }

                foreach (var choice in question.Choices.Where(choice => choice.Next is not null))
                {
                    CheckTransition(
                        choice.Next, order, page.PageId, question.QuestionId, problems);
                }
            }
        }
    }

    private static void CheckTransition(
        PageTransition? transition,
        IReadOnlyDictionary<string, int> order,
        string pageId,
        string? questionId,
        ImmutableArray<FlowProblem>.Builder problems)
    {
        if (transition is not { Kind: PageTransitionKind.Page } || transition.PageId is not { } target)
        {
            return;
        }

        if (!order.TryGetValue(target, out var targetIndex))
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.UnknownPage, pageId, questionId, target));
            return;
        }

        if (string.Equals(target, pageId, StringComparison.Ordinal))
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.SelfTransition, pageId, questionId, target));
            return;
        }

        // **前を向いた行き先を許すと、無限に回るアンケートが作れる**
        if (targetIndex <= order[pageId])
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.BackwardTransition, pageId, questionId, target));
        }
    }

    // ---- 表示条件 -----------------------------------------------------------

    private static void ValidateConditions(
        SurveyDefinition definition,
        ImmutableArray<FlowProblem>.Builder problems)
    {
        var byId = definition.AllQuestions
            .GroupBy(question => question.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // **並び順で「前」を決める。** ページの順番 → ページ内の順番
        var position = definition.Pages
            .SelectMany(page => page.Questions)
            .Select((question, index) => (question.QuestionId, index))
            .ToDictionary(entry => entry.QuestionId, entry => entry.index, StringComparer.Ordinal);

        foreach (var page in definition.Pages)
        {
            foreach (var question in page.Questions)
            {
                if (question.VisibleWhen is not { } condition || condition.IsEmpty)
                {
                    continue;
                }

                foreach (var rule in condition.Rules)
                {
                    ValidateRule(rule, question, page, byId, position, problems);
                }
            }
        }
    }

    private static void ValidateRule(
        ConditionRule rule,
        Question question,
        Page page,
        IReadOnlyDictionary<string, Question> byId,
        IReadOnlyDictionary<string, int> position,
        ImmutableArray<FlowProblem>.Builder problems)
    {
        if (!byId.TryGetValue(rule.QuestionId, out var referenced))
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.UnknownConditionQuestion,
                page.PageId,
                question.QuestionId,
                rule.QuestionId));
            return;
        }

        // **後ろを見る条件は成立しようがない。** その時点でまだ答えていない
        if (position[rule.QuestionId] >= position[question.QuestionId])
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.ForwardConditionReference,
                page.PageId,
                question.QuestionId,
                rule.QuestionId));
            return;
        }

        // **無い選択肢を見ている条件は、永久に成立しない。**
        // 選択肢の値を変えたときの直し忘れがここで見つかる
        if (rule.Operator is ConditionOperator.Equals or ConditionOperator.NotEquals
            && referenced.HasChoices
            && rule.Value is { } value
            && !referenced.Choices.Any(choice =>
                string.Equals(choice.Value, value, StringComparison.Ordinal)))
        {
            problems.Add(new FlowProblem(
                FlowProblemCode.UnknownChoiceValue,
                page.PageId,
                question.QuestionId,
                value));
        }
    }

    // ---- 到達できるか -------------------------------------------------------

    private static void ValidateReachability(
        SurveyDefinition definition,
        ImmutableArray<FlowProblem>.Builder problems)
    {
        if (definition.Pages.IsDefaultOrEmpty)
        {
            return;
        }

        // **答え方に関わらず「行き得る」ページを広げる。**
        // 実際に行けるかは回答次第なので、**行き得ないものだけを不備とする**
        var reachable = new HashSet<string>(StringComparer.Ordinal) { definition.Pages[0].PageId };
        var queue = new Queue<Page>();
        queue.Enqueue(definition.Pages[0]);

        var byId = definition.Pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        var order = definition.Pages
            .Select((page, index) => (page.PageId, index))
            .ToDictionary(entry => entry.PageId, entry => entry.index, StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var page = queue.Dequeue();

            foreach (var next in Destinations(page, definition, byId, order))
            {
                if (reachable.Add(next.PageId))
                {
                    queue.Enqueue(next);
                }
            }
        }

        foreach (var page in definition.Pages.Where(page => !reachable.Contains(page.PageId)))
        {
            problems.Add(new FlowProblem(FlowProblemCode.UnreachablePage, page.PageId));
        }
    }

    /// <summary>そのページから行き得る先。</summary>
    private static IEnumerable<Page> Destinations(
        Page page,
        SurveyDefinition definition,
        IReadOnlyDictionary<string, Page> byId,
        IReadOnlyDictionary<string, int> order)
    {
        var transitions = page.Questions
            .Where(question => question.HasChoiceTransitions)
            .SelectMany(question => question.Choices)
            .Select(choice => choice.Next)
            .ToList();

        // **選択肢に行き先が無い道も残る。** そのときはページ末尾の行き先に落ちる
        var everyChoiceJumps = transitions.Count > 0 && transitions.All(next => next is not null);
        if (!everyChoiceJumps)
        {
            transitions.Add(page.Next);
        }

        foreach (var transition in transitions)
        {
            switch (transition?.Kind)
            {
                case PageTransitionKind.Submit:
                    break;

                case PageTransitionKind.Page when transition.PageId is { } pageId:
                    if (byId.TryGetValue(pageId, out var target))
                    {
                        yield return target;
                    }

                    break;

                default:
                    var index = order[page.PageId] + 1;
                    if (index < definition.Pages.Length)
                    {
                        yield return definition.Pages[index];
                    }

                    break;
            }
        }
    }
}
