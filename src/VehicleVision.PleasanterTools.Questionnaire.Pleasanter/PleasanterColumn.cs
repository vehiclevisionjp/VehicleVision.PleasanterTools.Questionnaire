using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>Pleasanter の列の種別。</summary>
/// <remarks>
/// 標準は型ごとに 26 本（<c>A</c>〜<c>Z</c>）。項目拡張を有効にすると
/// <c>Class001</c> のような連番が加わる（<c>_documents/実機検証結果.md</c> 1 章）。
/// </remarks>
public enum PleasanterColumnKind
{
    Class,
    Num,
    Date,
    Description,
    Check,
    Attachments,
}

/// <summary>入れ物に入らず、レコード本体へ書き出す列。</summary>
public sealed record PleasanterRecordProperty(string Name, MappingTargetValueKind ValueKind);

/// <summary>列名から種別を判定する。</summary>
public static partial class PleasanterColumn
{
    private static readonly IReadOnlyDictionary<string, PleasanterRecordProperty> RecordPropertyByName =
        new Dictionary<string, PleasanterRecordProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = new("Title", MappingTargetValueKind.String),
            ["Body"] = new("Body", MappingTargetValueKind.String),
            ["Status"] = new("Status", MappingTargetValueKind.Integer),
            ["Manager"] = new("Manager", MappingTargetValueKind.Integer),
            ["Owner"] = new("Owner", MappingTargetValueKind.Integer),
            ["Locked"] = new("Locked", MappingTargetValueKind.Boolean),
            ["StartTime"] = new("StartTime", MappingTargetValueKind.DateTime),
            ["CompletionTime"] = new("CompletionTime", MappingTargetValueKind.DateTime),
            ["WorkValue"] = new("WorkValue", MappingTargetValueKind.Decimal),
            ["ProgressRate"] = new("ProgressRate", MappingTargetValueKind.Decimal),
            ["RemainingWorkValue"] = new("RemainingWorkValue", MappingTargetValueKind.Decimal),
        };

    /// <summary>入れ物に入らない書き込み先の一覧。</summary>
    public static IEnumerable<PleasanterRecordProperty> RecordProperties =>
        RecordPropertyByName.Values;

    /// <summary><c>ClassA</c> / <c>Class001</c> のどちらの形も受ける。</summary>
    [GeneratedRegex(
        "^(Class|Num|Date|Description|Check|Attachments)([A-Z]|[0-9]{3})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ColumnNamePattern { get; }

    /// <summary>列名から種別を判定する。判定できなければ <c>null</c>。</summary>
    public static PleasanterColumnKind? KindOf(string columnName)
    {
        if (string.IsNullOrEmpty(columnName))
        {
            return null;
        }

        var match = ColumnNamePattern.Match(columnName);
        return match.Success && Enum.TryParse<PleasanterColumnKind>(match.Groups[1].Value, out var kind)
            ? kind
            : null;
    }

    /// <summary>レコード本体へ書き出す列を取得する。</summary>
    public static PleasanterRecordProperty? RecordPropertyOf(string columnName) =>
        RecordPropertyByName.GetValueOrDefault(columnName);

    /// <summary>標準 26 列の枠を消費する列か。</summary>
    /// <remarks>レコード本体のプロパティは、型ごとにある <c>A</c>〜<c>Z</c> の列ではないため。</remarks>
    public static bool ConsumesColumnSlot(string columnName) =>
        RecordPropertyOf(columnName) is null;

    /// <summary>その種別が複数の値を保持できるか。</summary>
    /// <remarks>
    /// **どの種別も 1 列 1 値。** 複数選択を 1 列へ入れたい場合は、
    /// マッピングの段階で連結しておく（<c>_documents/アプリケーション設計.md</c> 7 章）。
    /// 添付だけは配列を取る。
    /// </remarks>
    public static bool AcceptsMultipleValues(PleasanterColumnKind kind) =>
        kind is PleasanterColumnKind.Attachments;

    /// <summary>添付列か。</summary>
    /// <remarks>
    /// **列名の決まりは Pleasanter 側の知識。**
    /// <c>.Core</c> の検査へはこの判定を渡して使う。
    /// </remarks>
    public static bool IsAttachment(string columnName) =>
        KindOf(columnName) is PleasanterColumnKind.Attachments;

    /// <summary><c>Class</c> 列の文字数上限。実機で確認した値。</summary>
    /// <remarks><c>nvarchar(2048 バイト)</c> ＝ 1024 文字（<c>_documents/実機検証結果.md</c> 1 章）。</remarks>
    public const int ClassMaxLength = 1024;
}
