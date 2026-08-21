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

    /// <summary>この設問を出す条件（Issue #41）。</summary>
    /// <remarks>
    /// **<c>null</c> なら常に出す。** ページを飛ばすのがジャンプ、
    /// 同じページの中で出し分けるのがこちら。
    /// **参照できるのは自分より前の設問だけ**（<c>SurveyFlowValidator</c> が弾く）。
    /// </remarks>
    public VisibilityCondition? VisibleWhen { get; init; }

    /// <summary>選択肢に行き先を持っているか。</summary>
    public bool HasChoiceTransitions =>
        !Choices.IsDefaultOrEmpty && Choices.Any(choice => choice.Next is not null);

    /// <summary>行き先を持てる形式か。**単一選択だけ。**</summary>
    public bool CanCarryTransitions => Type is QuestionType.Radio or QuestionType.Dropdown;

    /// <summary>選択肢を持つ形式か。</summary>
    /// <remarks>**グリッドとランキングも選択肢を持つ。** グリッドは列、ランキングは並べる項目。</remarks>
    public bool HasChoices =>
        Type is QuestionType.Radio or QuestionType.Checkbox or QuestionType.Dropdown
            or QuestionType.Grid or QuestionType.CheckboxGrid or QuestionType.Ranking;

    /// <summary>行を持つ形式か（Issue #54）。</summary>
    public bool HasRows => Type is QuestionType.Grid or QuestionType.CheckboxGrid;

    /// <summary>
    /// マッピングの入力を行ごとに出す形式か。
    /// </summary>
    /// <remarks>
    /// **グリッドは行ごと、ランキングは項目ごと。**
    /// どちらも「1 設問が複数の入力を出す」点で同じ扱いになる。
    /// </remarks>
    public bool HasRowPorts =>
        Type is QuestionType.Grid or QuestionType.CheckboxGrid or QuestionType.Ranking;

    /// <summary>マッピングの入力になる行（または項目）の識別子。</summary>
    public IEnumerable<string> RowPortIds =>
        Type switch
        {
            QuestionType.Grid or QuestionType.CheckboxGrid =>
                Settings.Rows.IsDefaultOrEmpty
                    ? []
                    : Settings.Rows.Select(row => row.RowId),

            // **ランキングは選択肢そのものが入力になる。** 値は順位
            QuestionType.Ranking =>
                Choices.IsDefaultOrEmpty ? [] : Choices.Select(choice => choice.Value),

            _ => [],
        };

    /// <summary>複数の値を受け取る形式か。</summary>
    /// <remarks>**ランキングは並べた順に全部返る**ので、複数値として扱う。</remarks>
    public bool IsMultiValue =>
        Type is QuestionType.Checkbox or QuestionType.CheckboxGrid or QuestionType.Ranking;

    /// <summary>回答を持たない表示専用の要素か。</summary>
    /// <remarks>
    /// <see cref="QuestionType.Note"/> は説明文ブロックで、Pleasanter の列へ写さない
    /// （<c>_documents/データモデル設計.md</c> 2.2）。
    /// </remarks>
    public bool IsDisplayOnly => Type is QuestionType.Note;

    /// <summary>説明文ブロックの本文を、書式の付いた形にしたもの（Issue #108）。</summary>
    /// <remarks>
    /// <para>
    /// **<see cref="Description"/> を記法として読んだ結果。**
    /// <see cref="QuestionType.Note"/> 以外では <c>null</c>。
    /// **持っている値は 1 つ（<c>Description</c>）だけ**で、こちらはそこから作る。
    /// 原文と構造を別々に保存すると、片方だけ直る事故が起きる。
    /// </para>
    /// <para>
    /// **画面はこれしか見ない。** 記法の解釈は
    /// <c>NoteMarkup</c> だけが行い、画面側に同じ実装を置かない。
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, ImmutableArray<NoteBlock>>? NoteBlocks
    {
        get
        {
            if (Type is not QuestionType.Note || Description is null)
            {
                return null;
            }

            var byLanguage = new Dictionary<string, ImmutableArray<NoteBlock>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var language in Description.Languages)
            {
                var blocks = Text.NoteMarkup.Parse(Description.Get(language));
                if (blocks.Length > 0)
                {
                    byLanguage[language] = blocks;
                }
            }

            return byLanguage.Count > 0 ? byLanguage : null;
        }
    }
}
