using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答画面の 1 ページ。ページの区切りがそのまま改ページになる。</summary>
public sealed record Page
{
    public required string PageId { get; init; }

    public LocalizedText? Title { get; init; }

    public LocalizedText? Description { get; init; }

    public ImmutableArray<Question> Questions { get; init; } = [];

    /// <summary>このページを終えたときの行き先（Issue #41）。</summary>
    /// <remarks>
    /// **<c>null</c> は「次のページへ」。** 分かれた道を合流させるために使う。
    /// **選択肢の行き先が優先される**（そちらが <c>null</c> のときにここへ落ちる）。
    /// </remarks>
    public PageTransition? Next { get; init; }

    /// <summary>ページの中の設問の順序を回答者ごとに入れ替えるか（Issue #103）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **同じページに出し分けの条件（<see cref="Question.VisibleWhen"/>）を持つ設問が
    /// あるときは指定できない**（<c>QuestionSettingsValidator</c> が公開の前に弾く）。
    /// 条件が参照できるのは自分より前の設問だけなので、
    /// **入れ替えると参照先が後ろへ回り、条件が成立しなくなる。**
    /// </para>
    /// <para>
    /// **説明文ブロック（<see cref="QuestionType.Note"/>）は動かさない。**
    /// 「以下の設問について」のような前置きが、説明する対象から離れてしまう。
    /// </para>
    /// <para>
    /// ⚠️ **並び順は 1 回の回答の中で固定する**（選択肢の入れ替えと同じ理由）。
    /// </para>
    /// </remarks>
    public bool ShuffleQuestions { get; init; }
}
