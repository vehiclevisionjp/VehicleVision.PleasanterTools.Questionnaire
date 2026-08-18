using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答画面の 1 ページ。ページの区切りがそのまま改ページになる。</summary>
public sealed record Page
{
    public required string PageId { get; init; }

    public LocalizedText? Title { get; init; }

    public LocalizedText? Description { get; init; }

    public ImmutableArray<Question> Questions { get; init; } = [];
}
