using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答画面へ返す候補 1 件（仕様書 4.2 節）。</summary>
/// <param name="CandidateKey">その応答内だけの識別子。Pleasanter の ID ではない。</param>
/// <param name="Display">モーダルに出す列（鍵は <c>DisplayColumns</c> の列名）。</param>
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
            foreach (var column in lookup.DisplayColumns.Where(allowed.Contains))
            {
                display[column] = row.Values.GetValueOrDefault(column);
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
                    && AnswerValidator.ValidateLookupValue(question, raw).IsEmpty)
                {
                    values[target.QuestionId] = raw;
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
}
