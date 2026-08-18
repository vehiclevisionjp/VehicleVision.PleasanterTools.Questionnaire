namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>添付ファイルのウイルススキャナ。</summary>
/// <remarks>
/// **実装は差し替え可能にする。** アプリはこのインターフェースだけを知る。
/// 本命は ClamAV（<c>clamd</c>）へ TCP で問い合わせる実装
/// （<c>_documents/非機能設計.md</c> 1 章）。
/// **ClamAV は GPL-2.0 なので製品へ同梱しない。** 別プロセスとして接続先を設定で受け取る。
/// </remarks>
public interface IVirusScanner
{
    /// <summary>中身を検査する。</summary>
    /// <exception cref="VirusScannerUnavailableException">
    /// スキャナへ到達できない場合。**「スキャンできなかったので通す」にしないこと。**
    /// </exception>
    Task<ScanVerdict> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>スキャンの判定。</summary>
public enum ScanVerdict
{
    /// <summary>検出なし。</summary>
    Clean,

    /// <summary>検出あり。</summary>
    Infected,
}

/// <summary>スキャナへ到達できない、または応答が不正。</summary>
public sealed class VirusScannerUnavailableException : Exception
{
    public VirusScannerUnavailableException(string message)
        : base(message)
    {
    }

    public VirusScannerUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
