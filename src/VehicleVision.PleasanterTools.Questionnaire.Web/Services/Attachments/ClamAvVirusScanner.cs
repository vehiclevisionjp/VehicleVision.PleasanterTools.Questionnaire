using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using nClam;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>ClamAV（<c>clamd</c>）へ TCP で問い合わせるスキャナ。</summary>
/// <remarks>
/// <para>
/// **ClamAV 本体は GPL-2.0。別プロセス（サイドカーコンテナ）として呼ぶだけにする。**
/// 同梱もリンクもしないので、本製品のデュアルライセンスは壊れない
/// （<c>LICENSING.md</c> / <c>_documents/添付ファイル検査-運用手順書.md</c> 3 章）。
/// 接続先は設定で受け取る。**イメージへ ClamAV を同梱しないこと。**
/// </para>
/// <para>
/// **判定がその場で付く。** 回答者を待たせるのはこの問い合わせの間だけ。
/// </para>
/// </remarks>
public sealed class ClamAvVirusScanner(
    VirusScanOptions options,
    ILogger<ClamAvVirusScanner> logger,
    Func<IClamClient>? clientFactory = null) : IVirusScanner
{
    private readonly Func<IClamClient> _clientFactory = clientFactory
        ?? (() => new ClamClient(options.ClamAvHost, options.ClamAvPort));

    public async Task<ScanVerdict> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        // **1 件あたりの上限を切る。** 応答が返らないまま回答者を待たせ続けない
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(options.ClamAvTimeout);

        ClamScanResult result;
        try
        {
            using var stream = AsStream(content);
            result = await _clientFactory()
                .SendAndScanFileAsync(stream, attempt.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VirusScannerUnavailableException(
                $"clamd が {options.ClamAvTimeout.TotalSeconds} 秒以内に応答しない");
        }
        catch (Exception exception) when (
            exception is SocketException
                or IOException
                or UnknownClamResponseException
                or MaxStreamSizeExceededException)
        {
            // **起動直後は定義データベースの取得が終わるまでスキャンできない。**
            // ここに来る間、添付は受け付けられない（_documents/添付ファイル検査-運用手順書.md 6 章）
            throw new VirusScannerUnavailableException(
                $"clamd（{options.ClamAvHost}:{options.ClamAvPort}）へ問い合わせられない", exception);
        }

        switch (result.Result)
        {
            case ClamScanResults.Clean:
                return ScanVerdict.Clean;

            case ClamScanResults.VirusDetected:
                // **検出名はサーバ側にだけ残す。** 回答者へは出さない（同 6 章）
                logger.LogWarning(
                    "添付からウイルスを検出した: {Detections}",
                    string.Join(
                        " / ",
                        result.InfectedFiles?.Select(file => file.VirusName) ?? ["不明"]));
                return ScanVerdict.Infected;

            default:
                // **判定が付かなかったものを「検出なし」にしない**
                throw new VirusScannerUnavailableException(
                    $"clamd が判定を返さなかった: {result.RawResult}");
        }
    }

    /// <summary>中身をコピーせずにストリームとして渡す。</summary>
    private static Stream AsStream(ReadOnlyMemory<byte> content) =>
        MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(content.ToArray(), writable: false);
}
