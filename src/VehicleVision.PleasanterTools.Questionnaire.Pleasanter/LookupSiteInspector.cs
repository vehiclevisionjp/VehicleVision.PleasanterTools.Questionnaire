using System.Collections.Immutable;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary><c>GetSite</c> の応答から、列と、統合・継承の有無を読む（Issue #879）。</summary>
/// <remarks>
/// 検索クライアントの <see cref="LookupSearchClient.InspectSiteAsync"/> が使う。
/// **値・API キーを扱わない。** 結果に生の応答を持たない。
/// </remarks>
internal static class LookupSiteInspector
{
    public static LookupSiteInspection Map(long siteId, JsonObject json)
    {
        if (json["Response"]?["Data"] is not JsonObject data
            || data["SiteSettings"] is not JsonObject settings
            || !TryLong(data["SiteId"], out var returnedSiteId)
            || returnedSiteId != siteId)
        {
            return Fail(LookupSearchStatus.SourceInvalid, LookupSearchFailure.InvalidResponse);
        }

        // 継承は、自サイトの ID と違えば。値が読めないときは安全側（継承とみなして拒否）
        var inherits = !TryLong(data["InheritPermission"], out var inherited) || inherited != siteId;
        var integrated = settings["IntegratedSites"] is JsonArray { Count: > 0 };

        var configured = new Dictionary<string, LookupSourceColumn>(StringComparer.Ordinal);
        if (settings["Columns"] is JsonArray columns)
        {
            foreach (var item in columns)
            {
                if (item is not JsonObject column
                    || column["ColumnName"] is not JsonValue nameValue
                    || !nameValue.TryGetValue<string>(out var name)
                    || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var choices = column["ChoicesText"] is JsonValue choicesValue
                    && choicesValue.TryGetValue<string>(out var choicesText)
                    && !string.IsNullOrWhiteSpace(choicesText);
                var nullable = column["Nullable"] is JsonValue nullableValue
                    && nullableValue.TryGetValue<bool>(out var nullableFlag)
                    && nullableFlag;
                int? places = column["DecimalPlaces"] is JsonValue placesValue
                    && placesValue.TryGetValue<int>(out var placesNumber)
                    ? placesNumber
                    : null;
                string? label = null;
                if (column["LabelText"] is JsonValue labelValue
                    && labelValue.TryGetValue<string>(out var labelText)
                    && !string.IsNullOrWhiteSpace(labelText)
                    && !string.Equals(labelText, name, StringComparison.Ordinal))
                {
                    label = labelText.Length > 100 ? labelText[..100] : labelText;
                }

                configured[name] = new LookupSourceColumn(
                    name, KindOf(name), choices, nullable, places, label);
            }
        }

        // 設定のない標準の列は、既定の列（選択肢・Nullable なし）として補う
        foreach (var name in StandardColumnNames())
        {
            configured.TryAdd(name, new LookupSourceColumn(name, KindOf(name)));
        }

        return new LookupSiteInspection
        {
            Status = LookupSearchStatus.Success,
            Columns = [.. configured.Values.OrderBy(column => column.Name, StringComparer.Ordinal)],
            IsIntegrated = integrated,
            InheritsPermission = inherits,
        };
    }

    public static LookupSiteInspection Fail(LookupSearchStatus status, LookupSearchFailure failure) =>
        new() { Status = status, Failure = failure };

    /// <summary>標準の列の名前。<c>Title</c>・<c>Body</c> と、型ごとの <c>A</c>〜<c>Z</c>（添付は対象外）。</summary>
    private static IEnumerable<string> StandardColumnNames()
    {
        yield return "Title";
        yield return "Body";
        foreach (var prefix in new[] { "Class", "Num", "Date", "Description", "Check" })
        {
            for (var letter = 'A'; letter <= 'Z'; letter++)
            {
                yield return prefix + letter;
            }
        }
    }

    private static LookupColumnKind KindOf(string name)
    {
        if (name is "Title" or "Body")
        {
            return LookupColumnKind.Text;
        }

        return PleasanterColumn.KindOf(name) switch
        {
            PleasanterColumnKind.Class or PleasanterColumnKind.Description => LookupColumnKind.Text,
            PleasanterColumnKind.Num => LookupColumnKind.Number,
            PleasanterColumnKind.Date => LookupColumnKind.Date,
            PleasanterColumnKind.Check => LookupColumnKind.Check,
            _ => LookupColumnKind.Other,
        };
    }

    private static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue(out value))
        {
            return true;
        }

        return json.TryGetValue<string>(out var text)
            && long.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
