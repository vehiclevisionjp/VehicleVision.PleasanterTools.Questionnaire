using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>マッピング先サイトの列だけを既存設定へ反映する。</summary>
/// <remarks>
/// <c>UpdateSite</c> は <c>SiteSettings</c> を丸ごと置き換えるため、呼び出し元が
/// <c>GetSite</c> の結果を渡す。ここでは対象列以外を削除も移動もせず、
/// スクリプト、スタイル、ビュー、通知、プロセス、権限などの設定も保持する。
/// </remarks>
public static partial class SiteSettingsSynchronizer
{
    /// <summary>現在のサイト設定へマッピング先の列を足し、表示順を整える。</summary>
    public static SiteSettingsSyncPlan Build(JsonNode response, MappingDefinition mapping)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(mapping);

        var settings = response["Response"]?["Data"]?["SiteSettings"]?.AsObject()
            ?? throw new InvalidOperationException("Pleasanter からサイト設定を取得できませんでした。");
        var synchronized = settings.DeepClone().AsObject();
        // Links は ChoicesText から Pleasanter が再構築する派生値。GetSite の値を返すと、
        // 空の配列で再構築結果を上書きしてしまうため送らない。
        synchronized.Remove("Links");
        var targets = mapping.Assignments
            .Select(assignment => assignment.TargetColumn)
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var columns = synchronized["Columns"]?.AsArray() ?? [];
        synchronized["Columns"] = columns;
        var existingColumns = columns
            .OfType<JsonObject>()
            .Select(column => column["ColumnName"]?.GetValue<string>())
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => column!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var target in targets)
        {
            if (existingColumns.Add(target))
            {
                columns.Add(new JsonObject
                {
                    ["ColumnName"] = target,
                    ["LabelText"] = target,
                });
                added.Add(target);
            }
        }

        var grid = Rearrange(synchronized, "GridColumns", targets);
        var history = Rearrange(synchronized, "HistoryColumns", targets);
        var editorHash = synchronized["EditorColumnHash"]?.AsObject() ?? new JsonObject();
        synchronized["EditorColumnHash"] = editorHash;
        foreach (var group in editorHash)
        {
            if (group.Value is JsonArray columnsInGroup)
            {
                RemoveTargets(columnsInGroup, targets);
            }
        }

        var general = editorHash["General"]?.AsArray() ?? [];
        editorHash["General"] = general;
        AddTargets(general, targets);

        return new SiteSettingsSyncPlan(
            synchronized,
            added,
            Strings(grid),
            Strings(general),
            Strings(history),
            [
                "マッピング対象外の列は、一覧・編集・履歴で現在の順序のままです。",
                "スクリプト、スタイル、ビュー、通知、プロセス、権限を含む列以外の設定には触れません。",
            ]);
    }

    /// <summary>対象列のリンク先として書かれているサイト ID を集める。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **<c>GetSite</c> は <c>Links</c> を返さない**（実機で確認。
    /// <c>ChoicesText</c> は <c>[[1]]</c> のまま戻るが <c>Links</c> は <c>null</c>）。
    /// **更新後に読み直してもリンクの成立を確かめられない。**
    /// </para>
    /// <para>
    /// 代わりに**リンク先のサイトを同じ API キーで引けるか**を見る。
    /// Pleasanter 側の <c>SetLinks</c> は**相手サイトへアクセスできるときだけ**
    /// リンクを作り、**できなければ黙って捨てる**ので、これが実際の判定条件そのもの。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<long> LinkedSiteIds(JsonNode response, IEnumerable<string> targets)
    {
        var wanted = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return response["Response"]?["Data"]?["SiteSettings"]?["Columns"]?.AsArray()
            ?.OfType<JsonObject>()
            .Where(column => column["ColumnName"]?.GetValue<string>() is { } name
                && wanted.Contains(name))
            .SelectMany(column => LinkedSiteIds(column["ChoicesText"]?.GetValue<string>()))
            .Distinct()
            .ToArray()
            ?? [];
    }

    private static IEnumerable<long> LinkedSiteIds(string? choicesText)
    {
        if (choicesText is null)
        {
            yield break;
        }

        foreach (Match match in LinkChoice().Matches(choicesText))
        {
            if (long.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var siteId) && siteId > 0)
            {
                yield return siteId;
            }
        }
    }

    [GeneratedRegex(@"\[\[(\d+)\]\]")]
    private static partial Regex LinkChoice();

    private static JsonArray Rearrange(JsonObject settings, string name, IReadOnlyList<string> targets)
    {
        var columns = settings[name]?.AsArray() ?? [];
        settings[name] = columns;
        RemoveTargets(columns, targets);
        AddTargets(columns, targets);
        return columns;
    }

    private static void RemoveTargets(JsonArray columns, IReadOnlyList<string> targets)
    {
        for (var index = columns.Count - 1; index >= 0; index--)
        {
            var column = columns[index]?.GetValue<string>();
            if (column is not null && targets.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                columns.RemoveAt(index);
            }
        }
    }

    private static void AddTargets(JsonArray columns, IEnumerable<string> targets)
    {
        var existing = Strings(columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (existing.Add(target))
            {
                columns.Add(target);
            }
        }
    }

    private static IReadOnlyList<string> Strings(JsonArray columns) =>
        columns.Select(column => column?.GetValue<string>())
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => column!)
            .ToArray();

}

/// <summary>同期前に画面へ表示する変更内容。</summary>
public sealed record SiteSettingsSyncPlan(
    JsonObject SiteSettings,
    IReadOnlyList<string> AddedColumns,
    IReadOnlyList<string> GridColumns,
    IReadOnlyList<string> EditorColumns,
    IReadOnlyList<string> HistoryColumns,
    IReadOnlyList<string> Unchanged);
