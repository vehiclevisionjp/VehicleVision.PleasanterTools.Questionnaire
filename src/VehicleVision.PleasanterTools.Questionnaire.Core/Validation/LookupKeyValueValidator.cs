using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>キー値の不備の種類（仕様書 5.3 節）。</summary>
public enum LookupKeyValueProblem
{
    /// <summary>キーが渡されていない（<c>null</c>）。</summary>
    Missing,

    /// <summary>空・空白のみ。**検索しない**（参照元は空条件を捨てて全件を返し得る）。</summary>
    Empty,

    /// <summary>長すぎる。</summary>
    TooLong,

    /// <summary>制御文字（NUL・タブ・改行など）を含む。</summary>
    ControlCharacter,

    /// <summary>許可文字以外（<c>%</c> <c>_</c> <c>[</c> <c>]</c> 引用符を含む）。</summary>
    DisallowedCharacter,

    /// <summary>整数の列に、整数に読めない値。</summary>
    NotInteger,

    /// <summary>前方一致の最短文字数に足りない。</summary>
    PrefixTooShort,

    /// <summary>キーにできない列の型。</summary>
    UnsupportedColumnKind,
}

/// <summary>キー値の検証結果。<see cref="Value"/> は <see cref="IsValid"/> のときだけ。</summary>
public readonly record struct LookupKeyValueResult(string? Value, LookupKeyValueProblem? Problem)
{
    public bool IsValid => Problem is null;
}

/// <summary>
/// 回答者が入れたキー値を、参照元へ渡せる形か検証する（Issue #828・仕様書 5.3 節）。
/// </summary>
/// <remarks>
/// <para>
/// **JSON のエスケープだけでは検索構文の注入を防げない**（Pleasanter は配列・範囲・予約値を解釈する）。
/// そのため許可文字を **許可リスト**で絞り、危険な値は外へ出さずに拒否する。
/// </para>
/// <para>
/// 検索都合の正規化（NFKC・数値への変換）はしない。**前後の空白を除くだけ。**
/// 数値キーも文字列のまま渡し、先頭ゼロを保つ。
/// </para>
/// </remarks>
public static class LookupKeyValueValidator
{
    /// <summary>キー値の最大長（文字数。サロゲートペアは 1 文字）。</summary>
    public const int MaxLength = 128;

    /// <summary>整数キーの最大桁数（符号を除く）。</summary>
    public const int MaxIntegerDigits = 18;

    public static LookupKeyValueResult Validate(
        string? raw,
        LookupColumnKind columnKind,
        LookupMatchMode matchMode)
    {
        if (raw is null)
        {
            return Fail(LookupKeyValueProblem.Missing);
        }

        if (columnKind is not (LookupColumnKind.Text or LookupColumnKind.Number))
        {
            return Fail(LookupKeyValueProblem.UnsupportedColumnKind);
        }

        // 制御文字は、空白の除去より先に拒否する（前後のタブ・改行を黙って捨てない）
        foreach (var rune in raw.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                return Fail(LookupKeyValueProblem.ControlCharacter);
            }
        }

        var value = raw.Trim();
        if (value.Length == 0)
        {
            return Fail(LookupKeyValueProblem.Empty);
        }

        var length = value.EnumerateRunes().Count();
        if (length > MaxLength)
        {
            return Fail(LookupKeyValueProblem.TooLong);
        }

        if (columnKind == LookupColumnKind.Number)
        {
            return matchMode == LookupMatchMode.Prefix
                ? Fail(LookupKeyValueProblem.UnsupportedColumnKind)
                : ValidateInteger(value);
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (!IsAllowed(rune))
            {
                return Fail(LookupKeyValueProblem.DisallowedCharacter);
            }
        }

        if (matchMode == LookupMatchMode.Prefix && length < LookupLimits.MinPrefixLength)
        {
            return Fail(LookupKeyValueProblem.PrefixTooShort);
        }

        return new LookupKeyValueResult(value, null);
    }

    /// <summary>英数字・ハイフン・内部の空白・日本語の文字だけを許す。</summary>
    private static bool IsAllowed(Rune rune) =>
        Rune.IsLetterOrDigit(rune)
        || rune.Value is '-' or ' ' or '　';

    private static LookupKeyValueResult ValidateInteger(string value)
    {
        var digits = value.StartsWith('-') ? value[1..] : value;
        if (digits.Length == 0 || digits.Any(character => character is < '0' or > '9'))
        {
            return Fail(LookupKeyValueProblem.NotInteger);
        }

        return digits.Length > MaxIntegerDigits
            ? Fail(LookupKeyValueProblem.TooLong)
            : new LookupKeyValueResult(value, null);
    }

    private static LookupKeyValueResult Fail(LookupKeyValueProblem problem) => new(null, problem);
}
