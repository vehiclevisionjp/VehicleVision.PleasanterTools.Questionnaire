using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>参照元の列の種類（Pleasanter の列の型の大分類）。</summary>
public enum LookupColumnKind
{
    /// <summary>文字列の列（<c>Class</c>・<c>Description</c> など）。</summary>
    Text,

    /// <summary>数値の列（<c>Num</c>）。</summary>
    Number,

    /// <summary>日付の列。</summary>
    Date,

    /// <summary>チェックの列。</summary>
    Check,

    /// <summary>上記以外（添付など）。</summary>
    Other,
}

/// <summary>検証に使う、参照元の列の情報。列定義を取得する側が組み立てて渡す。</summary>
/// <param name="Name">列の名前。</param>
/// <param name="Kind">列の種類。</param>
/// <param name="HasChoices">選択肢の設定があるか。</param>
/// <param name="Nullable">数値列の <c>Nullable</c> 設定。0 を保存・返却できるのはこの設定の列だけ。</param>
/// <param name="DecimalPlaces">数値列の小数の桁数。整数だけの列は 0。</param>
public sealed record LookupSourceColumn(
    string Name,
    LookupColumnKind Kind,
    bool HasChoices = false,
    bool Nullable = false,
    int? DecimalPlaces = null);

/// <summary>参照元の列の型の不備の種類（実測の判断 2・3 の決定。Issue #808）。</summary>
public enum LookupColumnProblemCode
{
    /// <summary>参照元に無い列を指している。</summary>
    ColumnNotFound,

    /// <summary>
    /// キーにできない列の型。**選択肢のない文字列列と、整数だけの数値列に限る。**
    /// 条件が黙って捨てられて全件が返る型があるため。
    /// </summary>
    KeyColumnTypeUnsupported,

    /// <summary>定義に固定したキー列の種類が、参照元の実際の列の種類と違う。</summary>
    KeyColumnKindMismatch,

    /// <summary>前方一致を許したキーが文字列列ではない。</summary>
    PrefixColumnNotText,

    /// <summary>
    /// 返却を許可した数値列が <c>Nullable: true</c> ではない。
    /// **0 が空として保存・返却され、反映されない。**
    /// </summary>
    NumberColumnNotNullable,

    /// <summary>入力先の設問の形式に対して、列の型が合わない。</summary>
    TargetColumnTypeIncompatible,
}

/// <summary>参照元の列の型の不備 1 件。</summary>
public sealed record LookupColumnProblem(
    string? LookupId, LookupColumnProblemCode Code, string? Detail = null);

/// <summary>参照元の列の型を見る（Issue #808・仕様書 5.3 節・実機検証結果 12 章）。</summary>
/// <remarks>
/// 構造の検証（<see cref="LookupValidator"/>）の後に呼ぶ。
/// **列の情報が取得できなかったときは呼ばず、有効化しない。**
/// </remarks>
public static class LookupColumnValidator
{
    public static ImmutableArray<LookupColumnProblem> Validate(
        SurveyDefinition definition,
        LookupDefinition lookup,
        IReadOnlyCollection<LookupSourceColumn> sourceColumns)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(sourceColumns);

        var columns = sourceColumns
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var problems = ImmutableArray.CreateBuilder<LookupColumnProblem>();

        foreach (var key in lookup.Keys)
        {
            if (!columns.TryGetValue(key.SourceColumn, out var column))
            {
                problems.Add(Problem(lookup, LookupColumnProblemCode.ColumnNotFound, key.SourceColumn));
                continue;
            }

            if (!IsKeyColumn(column))
            {
                problems.Add(Problem(
                    lookup, LookupColumnProblemCode.KeyColumnTypeUnsupported, key.SourceColumn));
            }

            if (key.SourceColumnKind is { } declared && declared != column.Kind)
            {
                problems.Add(Problem(
                    lookup, LookupColumnProblemCode.KeyColumnKindMismatch, key.SourceColumn));
            }

            if (key.MatchMode == LookupMatchMode.Prefix && column.Kind != LookupColumnKind.Text)
            {
                problems.Add(Problem(
                    lookup, LookupColumnProblemCode.PrefixColumnNotText, key.SourceColumn));
            }
        }

        foreach (var name in AllowedAndTargetColumns(lookup))
        {
            if (!columns.TryGetValue(name, out var column))
            {
                problems.Add(Problem(lookup, LookupColumnProblemCode.ColumnNotFound, name));
            }
            else if (column.Kind == LookupColumnKind.Number && !column.Nullable)
            {
                problems.Add(Problem(lookup, LookupColumnProblemCode.NumberColumnNotNullable, name));
            }
        }

        foreach (var target in lookup.Targets)
        {
            var question = definition.FindQuestion(target.QuestionId);
            if (question is null || !columns.TryGetValue(target.SourceColumn, out var column))
            {
                continue;
            }

            if (!IsCompatible(question.Type, column))
            {
                problems.Add(Problem(
                    lookup, LookupColumnProblemCode.TargetColumnTypeIncompatible, target.QuestionId));
            }
        }

        return problems.ToImmutable();
    }

    /// <summary>キーにできる列か。選択肢のない文字列列、または整数だけの数値列。</summary>
    private static bool IsKeyColumn(LookupSourceColumn column) =>
        column.Kind switch
        {
            LookupColumnKind.Text => !column.HasChoices,
            LookupColumnKind.Number => column.DecimalPlaces == 0 && !column.HasChoices,
            _ => false,
        };

    private static bool IsCompatible(QuestionType type, LookupSourceColumn column) =>
        type switch
        {
            QuestionType.Text => column.Kind is LookupColumnKind.Text or LookupColumnKind.Number,
            QuestionType.Radio or QuestionType.Dropdown => column.Kind is LookupColumnKind.Text,
            QuestionType.Scale => column.Kind is LookupColumnKind.Number,
            _ => false,
        };

    private static IEnumerable<string> AllowedAndTargetColumns(LookupDefinition lookup)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in lookup.AllowedColumns.IsDefault ? [] : lookup.AllowedColumns)
        {
            names.Add(name);
        }

        foreach (var target in lookup.Targets)
        {
            names.Add(target.SourceColumn);
        }

        return names;
    }

    private static LookupColumnProblem Problem(
        LookupDefinition lookup, LookupColumnProblemCode code, string detail) =>
        new(lookup.LookupId, code, detail);
}
