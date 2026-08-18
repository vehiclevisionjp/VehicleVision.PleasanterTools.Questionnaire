using System.Text.RegularExpressions;

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

/// <summary>列名から種別を判定する。</summary>
public static partial class PleasanterColumn
{
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

    /// <summary>その種別が複数の値を保持できるか。</summary>
    /// <remarks>
    /// **どの種別も 1 列 1 値。** 複数選択を 1 列へ入れたい場合は、
    /// マッピングの段階で連結しておく（<c>_documents/アプリケーション設計.md</c> 7 章）。
    /// 添付だけは配列を取る。
    /// </remarks>
    public static bool AcceptsMultipleValues(PleasanterColumnKind kind) =>
        kind is PleasanterColumnKind.Attachments;

    /// <summary><c>Class</c> 列の文字数上限。実機で確認した値。</summary>
    /// <remarks><c>nvarchar(2048 バイト)</c> ＝ 1024 文字（<c>_documents/実機検証結果.md</c> 1 章）。</remarks>
    public const int ClassMaxLength = 1024;
}
