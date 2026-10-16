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

    /// <summary>ページ下部の釦。未指定なら従来どおり常に表示し、有効にする。</summary>
    public PageButtons? Buttons { get; init; }
}

/// <summary>標準釦 1 つの出し分け。</summary>
public sealed record PageButtonCondition
{
    /// <summary>釦を表示する条件。未指定または空なら常に表示する。</summary>
    public VisibilityCondition? VisibleWhen { get; init; }

    /// <summary>釦を有効にする条件。未指定または空なら常に有効にする。</summary>
    public VisibilityCondition? EnabledWhen { get; init; }
}

/// <summary>ページ下部の釦。</summary>
public sealed record PageButtons
{
    public PageButtonCondition? Submit { get; init; }

    public PageButtonCondition? Next { get; init; }

    public PageButtonCondition? Previous { get; init; }

    public ImmutableArray<PageLinkButton> Links { get; init; } = [];
}

/// <summary>回答を保存せず、任意の URL へ移動する釦。</summary>
public sealed record PageLinkButton
{
    public required string ButtonId { get; init; }

    public required LocalizedText Label { get; init; }

    public required string Url { get; init; }

    public bool OpenInNewTab { get; init; }

    public int SortOrder { get; init; }

    public VisibilityCondition? VisibleWhen { get; init; }

    public VisibilityCondition? EnabledWhen { get; init; }

    /// <summary>画面へ出してよい絶対 URL か。</summary>
    public bool HasAllowedUrl =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));
}
