namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>検証エラーの種別。</summary>
/// <remarks>
/// **文言はここに持たない。** 画面側で多言語の文言へ変換する
/// （<c>_documents/画面設計.md</c> 3 章）。
/// </remarks>
public enum ValidationErrorCode
{
    /// <summary>必須なのに未回答。</summary>
    Required,

    /// <summary>文字数の上限を超えた。</summary>
    TooLong,

    /// <summary>選択肢に無い値。</summary>
    UnknownChoice,

    /// <summary>単一選択なのに複数の値が来た。</summary>
    MultipleValuesNotAllowed,

    /// <summary>数値として解釈できない。</summary>
    NotANumber,

    /// <summary>数値が範囲外。</summary>
    OutOfRange,

    /// <summary>日付・時刻として解釈できない。</summary>
    NotADateTime,

    /// <summary>メールアドレスの形式ではない。</summary>
    InvalidEmail,

    /// <summary>URL の形式ではない。</summary>
    InvalidUrl,

    /// <summary>定義に無い設問への回答が来た。</summary>
    UnknownQuestion,

    /// <summary>表示専用の要素に回答が来た。</summary>
    AnswerNotAllowed,

    /// <summary>「その他」を選んでいないのに自由記述が来た。</summary>
    OtherTextNotAllowed,

    /// <summary>グリッドで、答えていない行がある（Issue #54）。</summary>
    RowRequired,

    /// <summary>その設問に無い行へ答えている。</summary>
    UnknownRow,

    /// <summary>ランキングで、同じ項目が 2 回出てくる。</summary>
    DuplicateRank,

    /// <summary>選んだ数が下限に足りない（Issue #101）。</summary>
    TooFewSelections,

    /// <summary>選んだ数が上限を超えた（Issue #101）。</summary>
    TooManySelections,

    /// <summary>指定された形式（正規表現）に合わない（Issue #102）。</summary>
    PatternMismatch,
}
