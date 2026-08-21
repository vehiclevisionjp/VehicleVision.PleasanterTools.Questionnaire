namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>設問の形式ごとの固有設定。</summary>
/// <remarks>
/// 形式によって使う項目が違う。使わない項目は <c>null</c> のままにする。
/// </remarks>
public sealed record QuestionSettings
{
    /// <summary>文字数の上限。<see cref="QuestionType.Text"/> / <see cref="QuestionType.Paragraph"/>。</summary>
    public int? MaxLength { get; init; }

    /// <summary>入力欄のプレースホルダ。</summary>
    public LocalizedText? Placeholder { get; init; }

    /// <summary>既定値。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>尺度の下限。<see cref="QuestionType.Scale"/> / <see cref="QuestionType.Rating"/>。</summary>
    public int? ScaleMinimum { get; init; }

    /// <summary>尺度の上限。</summary>
    public int? ScaleMaximum { get; init; }

    /// <summary>尺度の下限に添えるラベル。</summary>
    public LocalizedText? ScaleMinimumLabel { get; init; }

    /// <summary>尺度の上限に添えるラベル。</summary>
    public LocalizedText? ScaleMaximumLabel { get; init; }

    /// <summary>数値の下限。</summary>
    public decimal? NumberMinimum { get; init; }

    /// <summary>数値の上限。</summary>
    public decimal? NumberMaximum { get; init; }

    /// <summary>入力の形式検証。</summary>
    public TextFormat? Format { get; init; }

    /// <summary>入力の形式を正規表現で確かめる（Issue #102）。</summary>
    /// <remarks>
    /// **値の全体が合うかを見る**（前後を暗黙に固定する）。部分一致にすると、
    /// 「数字 4 桁」のつもりの指定が「どこかに数字 4 桁があればよい」になってしまう。
    /// ⚠️ **後退戻りしない照合器で動かす**（<see cref="Validation.TextPattern"/>）。
    /// 使えない構文は公開の前に弾く。
    /// </remarks>
    public string? Pattern { get; init; }

    /// <summary>正規表現に合わないときに出す文言（Issue #102）。</summary>
    /// <remarks>
    /// **正規表現そのものを回答者へ見せない。** <c>^[0-9]{4}$</c> と出しても伝わらない。
    /// 空なら当たり障りのない既定の文言を使う。
    /// </remarks>
    public LocalizedText? PatternMessage { get; init; }

    /// <summary>選べる数の下限。複数選べる設問だけ（Issue #101）。</summary>
    /// <remarks>
    /// **未回答には効かない。** 「答えないか、下限まで選ぶか」であり、
    /// 答えさせたいなら <see cref="Question.IsRequired"/> を立てること。
    /// 両方を立てると「必須で、かつ下限まで」になる。
    /// </remarks>
    public int? MinSelections { get; init; }

    /// <summary>選べる数の上限。複数選べる設問だけ（Issue #101）。</summary>
    public int? MaxSelections { get; init; }

    /// <summary>添付できる個数の上限。<see cref="QuestionType.File"/>。</summary>
    public int? MaxFileCount { get; init; }

    /// <summary>添付 1 件あたりのサイズ上限（バイト）。</summary>
    /// <remarks>
    /// 添付は送信待ちの <c>PayloadJson</c> へ Base64 で載る。
    /// **上限が無いと DB が溢れる**（<c>_documents/非機能設計.md</c> 1 章）。
    /// </remarks>
    public long? MaxFileSizeBytes { get; init; }

    /// <summary>グリッドの行（Issue #54）。</summary>
    /// <remarks>
    /// **列（選択肢）は <c>Choices</c> の方。** 行はここ。
    /// **1 行が 1 つの入力になる**ので、行を増やすほど使える列が減る。
    /// </remarks>
    public System.Collections.Immutable.ImmutableArray<GridRow> Rows { get; init; } = [];

    /// <summary>埋め込み（Issue #104 / #107）。<see cref="QuestionType.Embed"/> で使う。</summary>
    /// <remarks>
    /// **配信元は運用側が許したものだけ**（<see cref="EmbedPolicy"/>）。
    /// 保存の時点で弾くので、ここへ入っている値は許された配信元のはず。
    /// **ただし画面へ出す前にもう一度確かめる**（設定は後から狭められる）。
    /// </remarks>
    public EmbedSource? Embed { get; init; }
}

/// <summary>文字列入力の形式検証。</summary>
public enum TextFormat
{
    /// <summary>検証しない。</summary>
    None,

    /// <summary>メールアドレスとして妥当か。</summary>
    Email,

    /// <summary>URL として妥当か。</summary>
    Url,
}
