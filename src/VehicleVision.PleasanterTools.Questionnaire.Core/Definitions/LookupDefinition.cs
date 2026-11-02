using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>ルックアップの参照元の種類。</summary>
public enum LookupSourceKind
{
    /// <summary>Pleasanter のサイト。</summary>
    Pleasanter,

    /// <summary>日本郵便 API。**未実装**（仕様書 12 章。検証は拒否する）。</summary>
    JapanPost,
}

/// <summary>キーの一致方式。回答者は変更できない。</summary>
public enum LookupMatchMode
{
    /// <summary>完全一致（基本）。意味は参照元 DB の照合順序に従う。</summary>
    Exact,

    /// <summary>前方一致。管理者がキーごとに許可したときだけ。</summary>
    Prefix,
}

/// <summary>ルックアップの上限（仕様書 3.2・11 章 U4）。</summary>
public static class LookupLimits
{
    /// <summary>キー項目の最大数（N）。</summary>
    public const int MaxKeys = 5;

    /// <summary>入力先項目の最大数（M）。</summary>
    public const int MaxTargets = 10;

    /// <summary>最大候補数 K の既定。</summary>
    public const int DefaultMaxResults = 10;

    /// <summary>最大候補数 K のシステム上限。</summary>
    public const int SystemMaxResults = 20;

    /// <summary>前方一致で必要な最短の文字数。</summary>
    public const int MinPrefixLength = 3;
}

/// <summary>キー項目と、参照元のキー列の対応。</summary>
public sealed record LookupKey
{
    /// <summary>回答画面の入力項目（設問）。</summary>
    public required string QuestionId { get; init; }

    /// <summary>参照元のキー列の名前。</summary>
    public required string SourceColumn { get; init; }

    /// <summary>
    /// 参照元のキー列の種類（<c>Text</c> か <c>Number</c>）。**公開時に版へ固定する。**
    /// 検索時に参照元へ問い合わせず、これで値の検証を型別にする。<c>null</c> は未指定で、公開前の検証で拒否する。
    /// </summary>
    public Validation.LookupColumnKind? SourceColumnKind { get; init; }

    public LookupMatchMode MatchMode { get; init; } = LookupMatchMode.Exact;

    /// <summary>前方一致を許すか。既定は許さない。</summary>
    public bool AllowPrefix { get; init; }
}

/// <summary>参照元の列と、結果を入れる入力先項目の対応。</summary>
public sealed record LookupTarget
{
    public required string SourceColumn { get; init; }

    public required string QuestionId { get; init; }
}

/// <summary>ルックアップ定義 1 つ。1 つの検索ボタンに対応する（仕様書 3.2 節）。</summary>
/// <remarks>
/// 秘密（API キー）は含めない。<see cref="CredentialId"/> は資格情報への参照だけ。
/// </remarks>
public sealed record LookupDefinition
{
    public required string LookupId { get; init; }

    /// <summary>ボタンを置く起点項目（設問）。</summary>
    public required string AnchorQuestionId { get; init; }

    public LocalizedText? ButtonLabel { get; init; }

    public LookupSourceKind SourceKind { get; init; } = LookupSourceKind.Pleasanter;

    /// <summary>参照元サイト ID。Pleasanter だけ。</summary>
    public long? SourceSiteId { get; init; }

    /// <summary>資格情報の ID。</summary>
    public string? CredentialId { get; init; }

    public ImmutableArray<LookupKey> Keys { get; init; } = [];

    public ImmutableArray<LookupTarget> Targets { get; init; } = [];

    /// <summary>回答者へ返せる列の許可リスト。</summary>
    public ImmutableArray<string> AllowedColumns { get; init; } = [];

    /// <summary>モーダルに出す列。許可列の部分集合。</summary>
    public ImmutableArray<string> DisplayColumns { get; init; } = [];

    /// <summary>1 回の最大候補数 K。</summary>
    public int MaxResults { get; init; } = LookupLimits.DefaultMaxResults;
}
