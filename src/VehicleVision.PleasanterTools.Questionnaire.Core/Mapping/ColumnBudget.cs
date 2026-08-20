using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>ある型の列を、いくつ使っていて、いくつ残っているか。</summary>
/// <param name="Prefix">列の接頭辞（<c>Class</c> / <c>Num</c> など）。</param>
/// <param name="Used">使っている本数。</param>
/// <param name="Available">使える本数。</param>
public sealed record ColumnUsage(string Prefix, int Used, int Available)
{
    /// <summary>残り。**足りていなければ負になる。**</summary>
    public int Remaining => Available - Used;

    /// <summary>足りているか。</summary>
    public bool Fits => Used <= Available;
}

/// <summary>Pleasanter の列がいくつ要るかを数える。</summary>
/// <remarks>
/// <para>
/// **列は型ごとに 26 本しかない**（<c>A</c>〜<c>Z</c>。
/// <c>_documents/実機検証結果.md</c>）。項目拡張で増やせるが、既定はこれ。
/// </para>
/// <para>
/// **グリッドは 1 設問で行数ぶんの列を食う**（Issue #54）。
/// 5 行のグリッドを 5 つ置けば、それだけで上限に届く。
/// </para>
/// <para>
/// **保存や公開のときに初めて足りないと分かると、作り直しになる**
/// （<c>_documents/機能一覧.md</c> 6 章）。
/// **編集している間ずっと見せるためにここがある。**
/// </para>
/// </remarks>
public static class ColumnBudget
{
    /// <summary>標準で使える本数。**型ごとに <c>A</c>〜<c>Z</c> の 26 本。**</summary>
    public const int StandardColumnsPerType = 26;

    /// <summary>列名から型の接頭辞を取り出す。</summary>
    /// <remarks>
    /// <c>ClassA</c> → <c>Class</c>、<c>Class012</c> → <c>Class</c>。
    /// **末尾の英数字を落とすだけ。** 型の一覧を持たないのは、
    /// 項目拡張で増えた列（<c>Class001</c> など）も同じ扱いにするため。
    /// </remarks>
    public static string PrefixOf(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var end = columnName.Length;
        while (end > 0 && char.IsAsciiLetterOrDigit(columnName[end - 1]))
        {
            // **接頭辞そのものも英数字。** 1 文字は必ず残す
            if (end - 1 == 0)
            {
                break;
            }

            var rest = columnName[..(end - 1)];
            if (rest.Length > 0 && char.IsAsciiDigit(columnName[end - 1]))
            {
                end--;
                continue;
            }

            // 末尾が英字のときは 1 文字だけ落とす（ClassA → Class）
            end--;
            break;
        }

        return columnName[..Math.Max(end, 1)];
    }

    /// <summary>今の割り当てで、型ごとに何本使っているか。</summary>
    public static ImmutableArray<ColumnUsage> Measure(
        MappingDefinition mapping,
        int availablePerType = StandardColumnsPerType)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return
        [
            .. mapping.Assignments
                .Select(assignment => assignment.TargetColumn)
                .Where(column => !string.IsNullOrWhiteSpace(column))
                .GroupBy(PrefixOf, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ColumnUsage(
                    group.Key,
                    group.Select(column => column).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    availablePerType)),
        ];
    }

    /// <summary>この定義を「行ごとに 1 列」で写すと、いくつ入力が要るか。</summary>
    /// <remarks>
    /// **見積もりであって、実際の消費ではない。**
    /// まとめて 1 列へ入れる人もいるので、**編集画面で「このままだと何本要るか」を
    /// 見せるための数**。
    /// </remarks>
    public static int RequiredPortCount(SurveyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.AllQuestions
            .Where(question => !question.IsDisplayOnly)
            .Sum(question => question.HasRowPorts ? Math.Max(question.RowPortIds.Count(), 1) : 1);
    }
}
