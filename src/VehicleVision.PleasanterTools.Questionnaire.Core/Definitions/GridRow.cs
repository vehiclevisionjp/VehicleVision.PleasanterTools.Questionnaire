namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>グリッドの行 1 つ。</summary>
/// <param name="RowId">
/// 行の識別子。**マッピングの入力を指すのに使う**（`MappingSource.RowId`）。
/// **設問の中で一意**であればよい。
/// </param>
/// <param name="Label">画面に出る文字列。</param>
/// <remarks>
/// **列（選択肢）は <see cref="Question.Choices"/> を使い回す。**
/// 行と列で別の型を作ると、片方にだけ「その他」や行き先が付いて食い違う。
/// </remarks>
public sealed record GridRow(string RowId, LocalizedText Label);
