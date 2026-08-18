using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>変換の種別。</summary>
public static class ConverterOperations
{
    /// <summary>入力をそのまま出す。</summary>
    public const string Identity = "identity";

    /// <summary>入力を連結して 1 つにする。設定 <c>separator</c>。</summary>
    public const string Join = "join";

    /// <summary>値を別の値へ置き換える。設定 <c>map.{元の値}</c> = 置き換え後。</summary>
    public const string Map = "map";

    /// <summary>設定 <c>value</c> が含まれていれば <c>true</c>。チェック列向け。</summary>
    public const string ToCheck = "toCheck";

    /// <summary>設定 <c>keyword</c> を含む値があれば <c>true</c>。</summary>
    public const string Contains = "contains";

    /// <summary>入力によらず設定 <c>value</c> を出す。</summary>
    public const string Constant = "constant";

    /// <summary>空でない最初の値を出す。</summary>
    public const string Coalesce = "coalesce";

    /// <summary>
    /// 設定 <c>when</c> と一致する入力があれば設定 <c>then</c>、無ければ設定 <c>else</c>。
    /// </summary>
    public const string When = "when";

    /// <summary>スクリプトで変換する。設定 <c>script</c>。</summary>
    /// <remarks>
    /// **固定の変換だけでは必ず足りなくなるための逃げ道**
    /// （<c>_documents/アーキテクチャ方針.md</c> 8 章）。
    /// 実行は <c>.Core</c> の外（スクリプトエンジン）が担う。
    /// **決定性を壊さないこと。** 時刻・乱数・外部 I/O を封じる。
    /// </remarks>
    public const string Script = "script";
}

/// <summary>スクリプト変換を実行する。</summary>
/// <remarks>
/// <c>.Core</c> は外部依存を持たないので、実装は外に置く。
/// 既定は Jint（JavaScript）。CLR アクセスを閉じ、実行時間・ステップ数・メモリの上限を掛ける。
/// </remarks>
public interface IScriptConverter
{
    /// <summary>スクリプトを適用する。</summary>
    /// <exception cref="ScriptConverterException">実行に失敗した場合。</exception>
    ImmutableArray<string> Convert(string script, ImmutableArray<string> input);
}

/// <summary>スクリプト変換の失敗。</summary>
public sealed class ScriptConverterException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>マッピングの不備（実行時）。</summary>
/// <param name="TargetColumn">対象の列。</param>
/// <param name="Reason">理由。</param>
public sealed record MappingRuntimeProblem(string TargetColumn, string Reason);

/// <summary>マッピングを適用した結果。</summary>
/// <param name="Columns">
/// 列名 → 値。**マッピングが管理している列はすべて含む。**
/// 値が無い場合は空配列で、これは「消す」を意味する。
/// </param>
/// <param name="Problems">実行時に見つかった不備。</param>
public sealed record MappingResult(
    ImmutableDictionary<string, ImmutableArray<string>> Columns,
    ImmutableArray<MappingRuntimeProblem> Problems);

/// <summary>マッピングを適用して、列名と値の対応を作る。</summary>
/// <remarks>
/// **評価は決定的。** 割り当てごとに入力を宣言順で集め、変換を 1 回だけ適用する
/// （<c>_documents/アプリケーション設計.md</c> 7 章）。
/// 値は文字列のまま返す。Pleasanter の型への変換は <c>.Pleasanter</c> 側の責務。
/// </remarks>
public sealed class MappingEvaluator(IScriptConverter? scriptConverter = null)
{
    public MappingResult Evaluate(
        MappingDefinition mapping,
        IReadOnlyCollection<Answer> answers)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(answers);

        var answerByQuestion = answers
            .GroupBy(answer => answer.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var columns = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            StringComparer.OrdinalIgnoreCase);
        var problems = ImmutableArray.CreateBuilder<MappingRuntimeProblem>();

        foreach (var assignment in mapping.Assignments)
        {
            if (!assignment.HasValidShape)
            {
                problems.Add(new MappingRuntimeProblem(
                    assignment.TargetColumn,
                    "入力が複数あるのに変換が無い、または入力が 1 つも無い"));
                continue;
            }

            if (columns.ContainsKey(assignment.TargetColumn))
            {
                // 同じ列への割り当てが 2 つ。保存時にも弾いているはず
                problems.Add(new MappingRuntimeProblem(
                    assignment.TargetColumn, "同じ列への割り当てが重複している"));
                continue;
            }

            // **宣言順に集める。** 並び順に依存した非決定性を作らない
            var input = ImmutableArray.CreateBuilder<string>();
            foreach (var source in assignment.Sources)
            {
                answerByQuestion.TryGetValue(source.QuestionId, out var answer);
                input.AddRange(Read(answer, source.Port));
            }

            try
            {
                // **管理している列は、値が無くても結果へ含める。** 空配列＝消す
                columns[assignment.TargetColumn] =
                    Apply(assignment.Converter, input.ToImmutable());
            }
            catch (ScriptConverterException exception)
            {
                problems.Add(new MappingRuntimeProblem(
                    assignment.TargetColumn, $"スクリプトの実行に失敗した: {exception.Message}"));
            }
        }

        return new MappingResult(columns.ToImmutable(), problems.ToImmutable());
    }

    private static ImmutableArray<string> Read(Answer? answer, QuestionPort port)
    {
        if (answer is null)
        {
            return [];
        }

        return port switch
        {
            QuestionPort.OtherText =>
                string.IsNullOrWhiteSpace(answer.OtherText) ? [] : [answer.OtherText],
            QuestionPort.FileNames => answer.FileNames.IsDefault ? [] : answer.FileNames,
            _ => answer.Values.IsDefault ? [] : answer.Values,
        };
    }

    private ImmutableArray<string> Apply(
        MappingConverter? converter,
        ImmutableArray<string> input)
    {
        if (converter is null)
        {
            return input;
        }

        switch (converter.Operation)
        {
            case ConverterOperations.Join:
                return input.IsEmpty
                    ? []
                    : [string.Join(converter.Config.GetValueOrDefault("separator", ","), input)];

            case ConverterOperations.Map:
                return
                [
                    .. input.Select(value =>
                        converter.Config.TryGetValue($"map.{value}", out var mapped) ? mapped : value),
                ];

            case ConverterOperations.ToCheck:
                var target = converter.Config.GetValueOrDefault("value", string.Empty);
                return [Bool(input.Contains(target, StringComparer.Ordinal))];

            case ConverterOperations.Contains:
                var keyword = converter.Config.GetValueOrDefault("keyword", string.Empty);
                return
                [
                    Bool(!string.IsNullOrEmpty(keyword)
                        && input.Any(value => value.Contains(keyword, StringComparison.Ordinal))),
                ];

            case ConverterOperations.Constant:
                return [converter.Config.GetValueOrDefault("value", string.Empty)];

            case ConverterOperations.Coalesce:
                var first = input.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return first is null ? [] : [first];

            case ConverterOperations.When:
                var when = converter.Config.GetValueOrDefault("when", string.Empty);
                var matched = input.Contains(when, StringComparer.Ordinal);
                var result = matched
                    ? converter.Config.GetValueOrDefault("then", string.Empty)
                    : converter.Config.GetValueOrDefault("else", string.Empty);
                return string.IsNullOrEmpty(result) ? [] : [result];

            case ConverterOperations.Script:
                var script = converter.Config.GetValueOrDefault("script", string.Empty);
                if (scriptConverter is null)
                {
                    throw new ScriptConverterException("スクリプト変換の実行環境が設定されていない");
                }

                return scriptConverter.Convert(script, input);

            case ConverterOperations.Identity:
            default:
                return input;
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
