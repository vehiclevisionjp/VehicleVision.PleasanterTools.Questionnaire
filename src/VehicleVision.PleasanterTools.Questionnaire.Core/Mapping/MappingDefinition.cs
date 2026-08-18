using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>設問のどの値を取り出すか。</summary>
public enum QuestionPort
{
    /// <summary>回答の値。</summary>
    Value,

    /// <summary>「その他」を選んだときの自由記述。</summary>
    OtherText,

    /// <summary>添付ファイルの名前。</summary>
    FileNames,

    /// <summary>添付ファイルそのもの。</summary>
    /// <remarks>
    /// <para>
    /// **添付列（<c>AttachmentsA</c> 等）へ入れるための口。**
    /// <see cref="FileNames"/> は名前だけなので、中身は運べない。
    /// </para>
    /// <para>
    /// **この口を使う割り当ては <c>1 : 0 : 1</c> に限る**
    /// （入力は添付の設問ちょうど 1 つ、変換なし、出力は添付列 1 本）。
    /// 添付に変換を掛ける必要は無いと決めた（2026-08-18）。
    /// </para>
    /// <para>
    /// **値の流れには乗せない。** 中身は Base64 で大きく、
    /// 文字列の配列として変換へ通す形にすると、他の列と同じ扱いができてしまう。
    /// </para>
    /// </remarks>
    Files,
}

/// <summary>割り当ての入力 1 つ。</summary>
/// <param name="QuestionId">設問。</param>
/// <param name="Port">その設問のどの値を取るか。</param>
public sealed record MappingSource(string QuestionId, QuestionPort Port = QuestionPort.Value);

/// <summary>入力を加工する変換。</summary>
/// <param name="Operation">変換の種別。<see cref="ConverterOperations"/>。</param>
/// <param name="Config">種別ごとの設定。</param>
public sealed record MappingConverter(
    string Operation,
    ImmutableDictionary<string, string> Config)
{
    public static MappingConverter Of(string operation, params (string Key, string Value)[] config) =>
        new(operation, config.ToImmutableDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}

/// <summary>Pleasanter の列 1 本への割り当て。</summary>
/// <remarks>
/// <para>
/// **取り得る形は 2 通りだけ。**
/// </para>
/// <list type="table">
///   <item>
///     <term><c>1 : 0 : 1</c></term>
///     <description>入力 1 つ、変換なし、出力 1 本。<see cref="Direct"/></description>
///   </item>
///   <item>
///     <term><c>N : 1 : 1</c></term>
///     <description>入力 N 個、変換 1 つ、出力 1 本。<see cref="Converted"/></description>
///   </item>
/// </list>
/// <para>
/// **入力が複数なら変換は必須。** どうまとめるかが決まらないため。
/// **出力が 1 本に固定されているので、合流の衝突も循環参照も構造的に起こらない。**
/// 評価順序は <see cref="Sources"/> の宣言順で決まるので、並び順に依存した非決定性も無い
/// （<c>_documents/アーキテクチャ方針.md</c> 8 章）。
/// </para>
/// <para>
/// 1 つの設問を複数の列へ写したい場合（複数選択 → 複数のチェック列）は、
/// **同じ設問を入力にした割り当てを列の数だけ並べる。**
/// </para>
/// </remarks>
public sealed record ColumnAssignment
{
    /// <summary>書き込み先の Pleasanter の列。</summary>
    public required string TargetColumn { get; init; }

    /// <summary>入力。**宣言した順に変換へ渡す。**</summary>
    public required ImmutableArray<MappingSource> Sources { get; init; }

    /// <summary>変換。<c>null</c> なら入力をそのまま書く（このとき入力は 1 つ）。</summary>
    public MappingConverter? Converter { get; init; }

    /// <summary><c>1 : 0 : 1</c>。入力をそのまま列へ写す。</summary>
    public static ColumnAssignment Direct(string targetColumn, MappingSource source) => new()
    {
        TargetColumn = targetColumn,
        Sources = [source],
        Converter = null,
    };

    /// <summary><c>N : 1 : 1</c>。入力を変換して列へ写す。</summary>
    public static ColumnAssignment Converted(
        string targetColumn,
        MappingConverter converter,
        params MappingSource[] sources) => new()
        {
            TargetColumn = targetColumn,
            Sources = [.. sources],
            Converter = converter,
        };

    /// <summary>取り得る 2 通りのどちらかになっているか。</summary>
    public bool HasValidShape =>
        Converter is not null
            ? !Sources.IsDefaultOrEmpty
            : Sources.Length == 1;
}

/// <summary>アンケート 1 つ分のマッピング。</summary>
public sealed record MappingDefinition
{
    public ImmutableArray<ColumnAssignment> Assignments { get; init; } = [];
}
