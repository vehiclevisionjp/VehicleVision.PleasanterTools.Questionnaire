using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答画面へ返す候補 1 件（仕様書 4.2 節）。</summary>
/// <param name="CandidateKey">その応答内だけの識別子。Pleasanter の ID ではない。</param>
/// <param name="Display">モーダルに出す列（鍵は定義の表示 ID。参照元の列名ではない。Issue #835）。</param>
/// <param name="Values">入力先の設問 ID → 値。**入れられる値だけ。**</param>
public sealed record LookupCandidateResponse(
    string CandidateKey,
    IReadOnlyDictionary<string, string?> Display,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>入力先の設問に入れられなかった値の警告。**値そのものは持たない。**</summary>
public sealed record LookupWarning(string CandidateKey, string FieldId, string Code);

/// <summary>検索成功の応答本文。</summary>
public sealed record LookupSearchResponse(
    int Version,
    string Outcome,
    IReadOnlyList<LookupCandidateResponse> Candidates,
    IReadOnlyList<LookupWarning> Warnings);

/// <summary>検索結果を、許可列だけの応答へ再射影する（Issue #832・仕様書 4.2・6.1 節）。</summary>
/// <remarks>
/// 上流の列選択に頼らず、**応答の直前に許可列と入力先の対応で絞り直す。**
/// 許可外の列・参照元のキー列の値・内部 ID が上流の応答に付いていても返さない。
/// </remarks>
public static class LookupResponseBuilder
{
    /// <summary>不適合の警告コード。</summary>
    public const string IncompatibleCode = "ValueIncompatible";

    /// <summary>decimal が丸めずに保てる有効桁の上限。</summary>
    private const int MaxSignificantDigits = 28;

    public static LookupSearchResponse Build(
        int version,
        SurveyDefinition survey,
        LookupDefinition lookup,
        LookupSearchResult result)
    {
        var allowed = lookup.AllowedColumns.ToHashSet(StringComparer.Ordinal);
        var questions = survey.AllQuestions
            .GroupBy(question => question.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var candidates = new List<LookupCandidateResponse>();
        var warnings = new List<LookupWarning>();
        var number = 0;
        foreach (var row in result.Candidates)
        {
            number++;
            var candidateKey = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var display = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var field in lookup.DisplayFields.Where(f => allowed.Contains(f.SourceColumn)))
            {
                display[field.DisplayId] = row.Values.GetValueOrDefault(field.SourceColumn);
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var target in lookup.Targets)
            {
                if (!allowed.Contains(target.SourceColumn))
                {
                    continue;
                }

                var raw = row.Values.GetValueOrDefault(target.SourceColumn);

                // **空の取得値は反映しない**（既存の入力を保持。決定 U3）
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                if (questions.TryGetValue(target.QuestionId, out var question)
                    && TryNormalize(question, target.SourceColumn, raw, out var normalized)
                    && AnswerValidator.ValidateLookupValue(question, normalized).IsEmpty)
                {
                    values[target.QuestionId] = normalized;
                }
                else
                {
                    warnings.Add(new LookupWarning(candidateKey, target.QuestionId, IncompatibleCode));
                }
            }

            candidates.Add(new LookupCandidateResponse(candidateKey, display, values));
        }

        return new LookupSearchResponse(version, result.Outcome ?? "notFound", candidates, warnings);
    }

    /// <summary>
    /// 取得値を、入力先の設問が受け取る表現へ整える（Issue #891・仕様書 4.1 節）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **数値列（<c>Num*</c>）だけが対象。** Pleasanter は小数点以下の桁数の設定どおりに <c>5.0000</c> を返すが、
    /// 回答画面の尺度・評価は選択肢の値の文字列一致で選択を決める。そこで、値が等しいまま末尾の 0 だけを落とす
    /// （<c>5.0000</c>→<c>5</c>、<c>1.2500</c>→<c>1.25</c>）。**丸め・単位除去はしない。**
    /// 文字列の列（郵便番号の先頭ゼロなど）は触らない。
    /// </para>
    /// <para>
    /// 尺度・評価は整数の選択肢で表すため、整数として解釈できない値（<c>5.5</c>）は反映しない。
    /// 数値として解釈できない値、有効桁が 28 桁を超える値（読み取りで丸まる）も反映しない。
    /// 範囲は呼び出し側の通常の検証（<see cref="AnswerValidator.ValidateLookupValue"/>）が見る。
    /// </para>
    /// </remarks>
    private static bool TryNormalize(Question question, string sourceColumn, string raw, out string normalized)
    {
        normalized = raw;
        if (PleasanterColumn.KindOf(sourceColumn) != PleasanterColumnKind.Num)
        {
            return true;
        }

        if (!decimal.TryParse(
                raw,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            || SignificantDigits(raw) > MaxSignificantDigits)
        {
            return false;
        }

        normalized = number == 0
            ? "0"
            : number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (normalized.Contains('.', StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('0').TrimEnd('.');
        }

        // 整数の選択肢で表す設問は、整数だけ受け取る
        return question.Type is not (QuestionType.Scale or QuestionType.Rating)
            || !normalized.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>先頭の 0 と小数部の末尾の 0 を除いた桁数。</summary>
    private static int SignificantDigits(string text)
    {
        var digits = text.Where(char.IsAsciiDigit).ToArray();
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var integerCount = point < 0
            ? digits.Length
            : text[..point].Count(char.IsAsciiDigit);
        var integer = digits.Take(integerCount).SkipWhile(c => c == '0').Count();
        var fraction = digits.Skip(integerCount).Reverse().SkipWhile(c => c == '0').Count();
        return integer + fraction;
    }
}
