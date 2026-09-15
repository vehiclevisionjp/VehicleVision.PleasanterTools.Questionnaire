using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>ページを離れるときの行き先。</summary>
/// <remarks>
/// <para>
/// **ジャンプ方式**（2026-08-19 決定。Issue #41）。
/// 選択肢ごとの行き先（<see cref="Choice.Next"/>）と、
/// ページ末尾の行き先（<see cref="Page.Next"/>）の 2 か所に置く。
/// </para>
/// <para>
/// **前へ戻す行き先を持たせない。** 許すと無限に回るアンケートを作れてしまう。
/// 検査で弾くのではなく、**戻れないことを構造で担保する**……とまではいかないので
/// （ページ ID は前後どちらも指せる）、公開時の検査で弾く（<c>SurveyFlowValidator</c>）。
/// </para>
/// </remarks>
public enum PageTransitionKind
{
    /// <summary>次のページへ。**既定。**</summary>
    Next,

    /// <summary>指定したページへ飛ぶ。</summary>
    Page,

    /// <summary>そこで終わり、送信へ進む。</summary>
    Submit,
}

/// <summary>行き先 1 つ。</summary>
/// <param name="Kind">行き先の種類。</param>
/// <param name="PageId">
/// <see cref="PageTransitionKind.Page"/> のときの飛び先。それ以外では <c>null</c>。
/// </param>
public sealed record PageTransition(PageTransitionKind Kind, string? PageId = null)
{
    /// <summary>次のページへ進む。</summary>
    public static readonly PageTransition Next = new(PageTransitionKind.Next);

    /// <summary>送信へ進む。</summary>
    public static readonly PageTransition Submit = new(PageTransitionKind.Submit);

    /// <summary>指定したページへ飛ぶ。</summary>
    public static PageTransition To(string pageId) => new(PageTransitionKind.Page, pageId);
}

/// <summary>表示条件の比べ方。</summary>
public enum ConditionOperator
{
    /// <summary>その値と等しい。**複数選択では「その値を含む」。**</summary>
    Equals,

    /// <summary>その値と等しくない。</summary>
    NotEquals,

    /// <summary>文字列としてその値を含む。</summary>
    Contains,

    /// <summary>何か答えている。</summary>
    Answered,

    /// <summary>何も答えていない。**飛ばされたページの設問もこちら。**</summary>
    NotAnswered,

    /// <summary>数として大きい。</summary>
    GreaterThan,

    /// <summary>数として小さい。</summary>
    LessThan,
}

/// <summary>条件を 1 つ。</summary>
/// <param name="QuestionId">見に行く設問。**自分より前にあるものだけ。**</param>
/// <param name="Operator">比べ方。</param>
/// <param name="Value">
/// 比べる値。<see cref="ConditionOperator.Answered"/> と
/// <see cref="ConditionOperator.NotAnswered"/> では使わない。
/// </param>
public sealed record ConditionRule(
    string QuestionId,
    ConditionOperator Operator,
    string? Value = null);

/// <summary>複数の条件のまとめ方。</summary>
public enum ConditionMatch
{
    /// <summary>すべて満たすとき。</summary>
    All,

    /// <summary>どれか 1 つでも満たすとき。</summary>
    Any,
}

/// <summary>表示条件。</summary>
/// <remarks>
/// <para>
/// **ジャンプではページの中の出し分けが書けない**ので、設問側に別に持たせる
/// （Issue #41）。ページを飛ばすのがジャンプ、同じページで出し分けるのがこちら。
/// </para>
/// <para>
/// **入れ子は今は作らない。** <c>All</c> と <c>Any</c> の 1 段で足りる場面がほとんどで、
/// 入れ子にすると編集画面が急に難しくなる。
/// **JSON の形は入れ子を足せるようにしてある**（規則の配列を持つだけなので）。
/// </para>
/// </remarks>
public sealed record VisibilityCondition
{
    /// <summary>まとめ方。既定はすべて満たすとき。</summary>
    public ConditionMatch Match { get; init; } = ConditionMatch.All;

    /// <summary>条件。**空なら常に出す**（条件が無いのと同じ）。</summary>
    public ImmutableArray<ConditionRule> Rules { get; init; } = [];

    /// <summary>条件を持っているか。</summary>
    public bool IsEmpty => Rules.IsDefaultOrEmpty;
}
