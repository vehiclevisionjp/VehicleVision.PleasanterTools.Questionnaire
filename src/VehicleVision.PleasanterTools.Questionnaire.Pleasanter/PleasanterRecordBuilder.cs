using System.Collections.Immutable;
using System.Globalization;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>列の値を Pleasanter の形へ写せなかった理由。</summary>
/// <param name="ColumnName">対象の列。</param>
/// <param name="Reason">理由。</param>
public sealed record ColumnConversionProblem(string ColumnName, string Reason);

/// <summary>Pleasanter へ送るレコードの中身。</summary>
/// <param name="Body">リクエストボディへ載せる内容。</param>
/// <param name="Problems">写せなかった列。</param>
public sealed record PleasanterRecord(
    IReadOnlyDictionary<string, object?> Body,
    ImmutableArray<ColumnConversionProblem> Problems);

/// <summary>マッピングの結果（列名 → 文字列）を Pleasanter のリクエストへ組み立てる。</summary>
/// <remarks>
/// <para>
/// <c>.Core</c> は列の値を文字列のまま返す。**型への変換はここが受け持つ**
/// （<c>_documents/アプリケーション設計.md</c> 7 章）。
/// </para>
/// <para>
/// **空の値は「消す」を意味する**（<c>_documents/アーキテクチャ方針.md</c> 8 章）。
/// 型ごとに、消したことが伝わる値を入れる。
/// </para>
/// </remarks>
public sealed class PleasanterRecordBuilder(PleasanterDateTime dateTime)
{
    public PleasanterRecord Build(
        IReadOnlyDictionary<string, ImmutableArray<string>> columns,
        string? responseJsonColumn = null,
        string? responseJson = null)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var hashes = new Dictionary<PleasanterColumnKind, Dictionary<string, object?>>();
        var problems = ImmutableArray.CreateBuilder<ColumnConversionProblem>();

        foreach (var (columnName, values) in columns)
        {
            var kind = PleasanterColumn.KindOf(columnName);
            if (kind is null)
            {
                problems.Add(new ColumnConversionProblem(columnName, "Pleasanter の列名として解釈できない"));
                continue;
            }

            if (kind is PleasanterColumnKind.Attachments)
            {
                // 添付は Base64 を伴うので、マッピングの出力からは組み立てない
                problems.Add(new ColumnConversionProblem(
                    columnName, "添付列はマッピングの出力先にできない"));
                continue;
            }

            if (!TryConvert(kind.Value, columnName, values, problems, out var converted))
            {
                continue;
            }

            if (!hashes.TryGetValue(kind.Value, out var hash))
            {
                hash = new Dictionary<string, object?>(StringComparer.Ordinal);
                hashes[kind.Value] = hash;
            }

            hash[columnName] = converted;
        }

        // 回答の正本。**マッピングとは別枠で入れる**（予約列）
        if (!string.IsNullOrEmpty(responseJsonColumn) && responseJson is not null)
        {
            var kind = PleasanterColumn.KindOf(responseJsonColumn);
            if (kind is PleasanterColumnKind.Description)
            {
                if (!hashes.TryGetValue(PleasanterColumnKind.Description, out var hash))
                {
                    hash = new Dictionary<string, object?>(StringComparer.Ordinal);
                    hashes[PleasanterColumnKind.Description] = hash;
                }

                hash[responseJsonColumn] = responseJson;
            }
            else
            {
                problems.Add(new ColumnConversionProblem(
                    responseJsonColumn, "回答 JSON の保存先は Description 系の列にすること"));
            }
        }

        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (kind, hash) in hashes)
        {
            body[$"{kind}Hash"] = hash;
        }

        return new PleasanterRecord(body, problems.ToImmutable());
    }

    private bool TryConvert(
        PleasanterColumnKind kind,
        string columnName,
        ImmutableArray<string> values,
        ImmutableArray<ColumnConversionProblem>.Builder problems,
        out object? converted)
    {
        converted = null;

        if (values.Length > 1)
        {
            // **黙って先頭を採らない。** 連結が要るならマッピング側で変換を挟むべき
            problems.Add(new ColumnConversionProblem(
                columnName, $"値が {values.Length} 個ある。1 列には 1 値しか入らない"));
            return false;
        }

        var value = values.IsDefaultOrEmpty ? null : values[0];

        switch (kind)
        {
            case PleasanterColumnKind.Class:
                var text = value ?? string.Empty;
                if (text.Length > PleasanterColumn.ClassMaxLength)
                {
                    // **切らずに拒否する。** 黙って切ると回答の一部が失われたことに気づけない
                    problems.Add(new ColumnConversionProblem(
                        columnName,
                        $"{PleasanterColumn.ClassMaxLength} 文字を超えている（{text.Length} 文字）"));
                    return false;
                }

                converted = text;
                return true;

            case PleasanterColumnKind.Description:
                converted = value ?? string.Empty;
                return true;

            case PleasanterColumnKind.Check:
                converted = value is not null
                    && bool.TryParse(value, out var flag)
                    && flag;
                return true;

            case PleasanterColumnKind.Num:
                if (string.IsNullOrWhiteSpace(value))
                {
                    converted = null;
                    return true;
                }

                if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    problems.Add(new ColumnConversionProblem(columnName, $"数値として読めない: {value}"));
                    return false;
                }

                converted = number;
                return true;

            case PleasanterColumnKind.Date:
                if (string.IsNullOrWhiteSpace(value))
                {
                    converted = null;
                    return true;
                }

                var asDateOnly = PleasanterDateTime.DateOnlyToPleasanter(value);
                if (asDateOnly is not null)
                {
                    converted = asDateOnly;
                    return true;
                }

                if (DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
                {
                    converted = dateTime.ToPleasanter(moment);
                    return true;
                }

                problems.Add(new ColumnConversionProblem(columnName, $"日時として読めない: {value}"));
                return false;

            default:
                problems.Add(new ColumnConversionProblem(columnName, "対応していない列種別"));
                return false;
        }
    }
}
