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
}
