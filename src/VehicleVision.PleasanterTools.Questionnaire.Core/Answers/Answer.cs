using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Answers;

/// <summary>設問 1 つに対する回答。</summary>
/// <param name="QuestionId">対象の設問。</param>
/// <param name="Values">
/// 回答の値。単一選択・記述式でも配列で持つ。未回答は空配列。
/// </param>
public sealed record Answer(string QuestionId, ImmutableArray<string> Values)
{
    /// <summary>「その他」を選んだときの自由記述。</summary>
    /// <remarks>
    /// **マッピングの入力になる**（入力ノードの <c>other</c> ポート）。
    /// グラフの外に置くと、どこへ写すかを設定できなくなる。
    /// </remarks>
    public string? OtherText { get; init; }

    /// <summary>添付ファイルの名前。</summary>
    /// <remarks>入力ノードの <c>files</c> ポートから取り出せる。</remarks>
    public ImmutableArray<string> FileNames { get; init; } = [];

    /// <summary>行ごとの回答（Issue #54）。</summary>
    /// <remarks>
    /// <para>
    /// **グリッドで使う。** 行の識別子 → その行で選ばれた値。
    /// 行ごとに 1 つの入力になるので、マッピングは行を指して列へ繋げる。
    /// </para>
    /// <para>
    /// **ランキングはここを使わない。** 並べた順そのものが答えなので、
    /// <see cref="Values"/> に順位の順で入る。順位はマッピングが取り出す。
    /// </para>
    /// </remarks>
    public ImmutableDictionary<string, ImmutableArray<string>> Rows { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>その行の値。**無ければ空。**</summary>
    public ImmutableArray<string> Row(string rowId) =>
        Rows.TryGetValue(rowId, out var values) ? values : [];

    /// <summary>ランキングでのその項目の順位。**選ばれていなければ <c>null</c>。**</summary>
    /// <remarks>**1 から数える。** 0 始まりだと画面に出したときに読み違える。</remarks>
    public int? RankOf(string value)
    {
        if (Values.IsDefaultOrEmpty)
        {
            return null;
        }

        var index = Values.IndexOf(value);
        return index < 0 ? null : index + 1;
    }

    /// <summary>値を 1 つも持たないか。</summary>
    /// <remarks>**行ごとの回答も見る。** グリッドは <see cref="Values"/> を使わない。</remarks>
    public bool IsEmpty =>
        (Values.IsDefaultOrEmpty || Values.All(string.IsNullOrWhiteSpace))
        && Rows.Values.All(values => values.IsDefaultOrEmpty || values.All(string.IsNullOrWhiteSpace));

    /// <summary>単一の値を返す。無ければ <c>null</c>。</summary>
    public string? SingleValue =>
        Values.IsDefaultOrEmpty ? null : Values[0];

    public static Answer Of(string questionId, params string[] values) =>
        new(questionId, [.. values]);
}
