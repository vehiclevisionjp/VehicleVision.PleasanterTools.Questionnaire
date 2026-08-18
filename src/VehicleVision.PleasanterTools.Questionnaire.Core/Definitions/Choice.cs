namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>選択肢 1 つ。</summary>
/// <param name="Value">保存される値。Pleasanter の列へ写るのはこちら。</param>
/// <param name="Label">画面に出る文字列。</param>
/// <param name="IsOther">「その他」（自由記述を伴う選択肢）かどうか。</param>
public sealed record Choice(string Value, LocalizedText Label, bool IsOther = false);
