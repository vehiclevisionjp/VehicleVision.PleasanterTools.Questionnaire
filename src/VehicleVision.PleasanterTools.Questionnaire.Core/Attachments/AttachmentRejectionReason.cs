namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>添付を受け付けなかった理由。</summary>
public enum AttachmentRejectionReason
{
    /// <summary>許可リストに無い拡張子。</summary>
    ExtensionNotAllowed,

    /// <summary>拡張子と中身の先頭バイトが一致しない。</summary>
    ContentDoesNotMatchExtension,

    /// <summary>サイズの上限を超えた。</summary>
    TooLarge,

    /// <summary>個数の上限を超えた。</summary>
    TooMany,

    /// <summary>
    /// 1 回の送信の合計サイズが上限を超えた。
    /// **1 件あたりの上限だけでは DB が溢れる**（設問の数だけ並べられる）。
    /// </summary>
    TotalTooLarge,

    /// <summary>ファイル名が不正（空、パス区切りを含む等）。</summary>
    InvalidFileName,

    /// <summary>ウイルスを検出した。</summary>
    Infected,

    /// <summary>
    /// スキャンが有効なのにスキャナへ到達できない。
    /// **「スキャンできなかったので通す」にしない。**
    /// </summary>
    ScannerUnavailable,
}
