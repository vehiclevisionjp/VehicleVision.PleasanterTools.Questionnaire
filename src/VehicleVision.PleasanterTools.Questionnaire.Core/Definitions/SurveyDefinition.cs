using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>公開時に固めたアンケート定義のスナップショット。</summary>
/// <remarks>
/// **不変。** 編集は下書き側で行い、公開するまで実行へ影響させない。
/// 回答レコードは <see cref="Version"/> を持ち、どの定義で作られたかを残す
/// （<c>_documents/データモデル設計.md</c> 1 章）。
/// </remarks>
public sealed record SurveyDefinition
{
    public required string SurveyId { get; init; }

    /// <summary>この定義の版。回答レコードへ記録する。</summary>
    public required int Version { get; init; }

    public required LocalizedText Title { get; init; }

    public LocalizedText? Description { get; init; }

    public ImmutableArray<Page> Pages { get; init; } = [];

    /// <summary>回答画面の表示モード。</summary>
    public DisplayMode DisplayMode { get; init; } = DisplayMode.Paged;

    /// <summary>進捗バーを出すか。</summary>
    public bool ShowProgress { get; init; } = true;

    /// <summary>送信後に出す文言。</summary>
    public LocalizedText? ConfirmationMessage { get; init; }

    /// <summary>回答の編集を許すか。</summary>
    public bool AllowEditingAfterSubmit { get; init; } = true;

    /// <summary>全ページの設問を順に返す。</summary>
    public IEnumerable<Question> AllQuestions => Pages.SelectMany(page => page.Questions);

    /// <summary>指定した ID の設問を返す。無ければ <c>null</c>。</summary>
    public Question? FindQuestion(string questionId) =>
        AllQuestions.FirstOrDefault(question => question.QuestionId == questionId);
}

/// <summary>回答画面の表示モード。</summary>
public enum DisplayMode
{
    /// <summary>ページ単位で表示する。</summary>
    Paged,

    /// <summary>1 問ずつ表示する。</summary>
    OneQuestionPerPage,
}
