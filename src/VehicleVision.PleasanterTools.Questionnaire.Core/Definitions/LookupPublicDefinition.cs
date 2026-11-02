using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答画面へ渡すルックアップ定義（Issue #822・仕様書 3.1 節）。</summary>
/// <remarks>
/// <para>
/// **操作に必要な情報だけ。** 検索 ID・起点・キー項目 ID・入力先 ID・ボタン文言のみ。
/// **参照元の種類・サイト ID・資格情報の参照・参照元の列名・許可列・件数の上限・前方一致の可否を含めない。**
/// 回答者に参照元の構造を教えないため。
/// </para>
/// <para>
/// **スナップショット（<see cref="SurveyDefinition"/>）をそのまま回答画面へ返さない。**
/// 必ずこの形へ写してから返す。
/// </para>
/// </remarks>
public sealed record LookupPublicDefinition
{
    public required string LookupId { get; init; }

    /// <summary>ボタンを置く起点項目（設問）。</summary>
    public required string AnchorQuestionId { get; init; }

    /// <summary>ボタン文言。<c>null</c> なら画面カタログの標準文言。</summary>
    public LocalizedText? ButtonLabel { get; init; }

    /// <summary>検索に必要なキー項目（設問 ID）。</summary>
    public ImmutableArray<string> KeyQuestionIds { get; init; } = [];

    /// <summary>結果を入れる入力先項目（設問 ID）。</summary>
    public ImmutableArray<string> TargetQuestionIds { get; init; } = [];

    /// <summary>サーバー用の定義から、回答画面用へ写す。</summary>
    public static LookupPublicDefinition From(LookupDefinition lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        return new LookupPublicDefinition
        {
            LookupId = lookup.LookupId,
            AnchorQuestionId = lookup.AnchorQuestionId,
            ButtonLabel = lookup.ButtonLabel,
            KeyQuestionIds = [.. lookup.Keys.Select(key => key.QuestionId)],
            TargetQuestionIds = [.. lookup.Targets.Select(target => target.QuestionId)],
        };
    }

    /// <summary>定義が持つルックアップを、回答画面用へ写す。無ければ空。</summary>
    public static ImmutableArray<LookupPublicDefinition> FromDefinition(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return [.. definition.GetLookups().Select(From)];
    }
}
