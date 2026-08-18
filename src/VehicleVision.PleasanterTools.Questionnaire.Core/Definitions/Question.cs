using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>設問 1 つ。公開時のスナップショットに含まれる不変の定義。</summary>
public sealed record Question
{
    public required string QuestionId { get; init; }

    public required QuestionType Type { get; init; }

    public required LocalizedText Title { get; init; }

    public LocalizedText? Description { get; init; }

    /// <summary>必須入力か。</summary>
    /// <remarks><see cref="QuestionType.Note"/> では常に <c>false</c> として扱う。</remarks>
    public bool IsRequired { get; init; }

    public ImmutableArray<Choice> Choices { get; init; } = [];

    public QuestionSettings Settings { get; init; } = new();

    /// <summary>選択肢を持つ形式か。</summary>
    public bool HasChoices =>
        Type is QuestionType.Radio or QuestionType.Checkbox or QuestionType.Dropdown;

    /// <summary>複数の値を受け取る形式か。</summary>
    public bool IsMultiValue => Type is QuestionType.Checkbox;

    /// <summary>回答を持たない表示専用の要素か。</summary>
    /// <remarks>
    /// <see cref="QuestionType.Note"/> は説明文ブロックで、Pleasanter の列へ写さない
    /// （<c>_documents/データモデル設計.md</c> 2.2）。
    /// </remarks>
    public bool IsDisplayOnly => Type is QuestionType.Note;
}
