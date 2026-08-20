namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>設問の形式。</summary>
/// <remarks>
/// 第 1 弾の対象は <c>_documents/機能一覧.md</c> の ◎ のみ。
/// グリッド・ランキング・NPS は後続で追加する。
/// </remarks>
public enum QuestionType
{
    /// <summary>記述式（1 行）。</summary>
    Text,

    /// <summary>段落（長文）。</summary>
    Paragraph,

    /// <summary>ラジオボタン（単一選択）。</summary>
    Radio,

    /// <summary>チェックボックス（複数選択）。</summary>
    Checkbox,

    /// <summary>プルダウン。</summary>
    Dropdown,

    /// <summary>直線尺度（1〜5 など）。</summary>
    Scale,

    /// <summary>星評価。</summary>
    Rating,

    /// <summary>日付。</summary>
    Date,

    /// <summary>時刻。</summary>
    Time,

    /// <summary>ファイル添付。</summary>
    File,

    /// <summary>選択グリッド（行列・行ごとに 1 つ選ぶ）。</summary>
    /// <remarks>
    /// **1 設問が行数ぶんの列を食い得る**（Issue #54）。
    /// どう写すかはマッピングが決める。**行ごとに 1 つの入力を出す**ので、
    /// 行ごとに列へ繋ぐことも、まとめて 1 列へ入れることもできる。
    /// </remarks>
    Grid,

    /// <summary>チェックボックスグリッド（行列・行ごとに複数選ぶ）。</summary>
    CheckboxGrid,

    /// <summary>ランキング（順位付け）。</summary>
    /// <remarks>
    /// 回答は**選択肢を順位の順に並べたもの**。
    /// マッピングでは選択肢ごとに順位を取り出せる。
    /// </remarks>
    Ranking,

    /// <summary>説明文ブロック。設問ではなく、Pleasanter の列へ写さない。</summary>
    Note,
}
