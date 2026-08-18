using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

/// <summary>設問 1 つに対する回答。</summary>
/// <param name="QuestionId">対象の設問。</param>
/// <param name="Values">
/// 回答の値。単一選択・記述式でも配列で持つ。未回答は空配列。
/// </param>
public sealed record Answer(string QuestionId, ImmutableArray<string> Values)
{
    /// <summary>「その他」を選んだときの自由記述。</summary>
    public string? OtherText { get; init; }

    /// <summary>値を 1 つも持たないか。</summary>
    public bool IsEmpty =>
        Values.IsDefaultOrEmpty || Values.All(string.IsNullOrWhiteSpace);

    /// <summary>単一の値を返す。無ければ <c>null</c>。</summary>
    public string? SingleValue =>
        Values.IsDefaultOrEmpty ? null : Values[0];

    public static Answer Of(string questionId, params string[] values) =>
        new(questionId, [.. values]);
}
