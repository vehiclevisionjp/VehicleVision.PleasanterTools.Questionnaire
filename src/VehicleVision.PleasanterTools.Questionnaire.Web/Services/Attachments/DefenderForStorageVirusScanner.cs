using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>Microsoft Defender for Storage に検査させるスキャナ。</summary>
/// <remarks>
/// <para>
/// **Azure に寄せる場合の選択肢**（<c>_documents/添付ファイル検査-運用手順書.md</c> 4 章）。
/// DMZ コンテナへ置き、Event Grid で届く判定を待つ。
/// **判定は非同期。** その間、回答者を待たせる。
/// </para>
/// <para>
/// **結果が出るまで通さない（default deny）。** 「タグが無い＝まだ検査されていない」を
/// 「通してよい」と解釈しない。時間切れ・エラー・未検査は、いずれも受け付けない。
/// </para>
/// <para>
/// **判定の待ち合わせはインスタンスの中で完結する**（<see cref="MalwareScanVerdicts"/>）。
/// スケールアウトすると通知が別のインスタンスへ届いて待てなくなる。
/// **既定の方式は ClamAV。** こちらは Azure のエコシステムに寄せたい場合に使う。
/// </para>
/// </remarks>
public sealed class DefenderForStorageVirusScanner(
    VirusScanOptions options,
    IDmzBlobStore blobs,
    MalwareScanVerdicts verdicts,
    ILogger<DefenderForStorageVirusScanner> logger) : IVirusScanner
{
    /// <summary>検出なし。</summary>
    private const string NoThreatsFound = "No threats found";

    /// <summary>検出あり。</summary>
    private const string Malicious = "Malicious";

    public async Task<ScanVerdict> ScanAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        // **推測できない名前にし、拡張子も付けない。** DMZ の中身から回答者をたどらせない
        var blobName = Guid.NewGuid().ToString("N");

        // **アップロードより先に待ち合わせへ登録する。** 後だと通知を取りこぼす
        using var registration = verdicts.Register(blobName);

        try
        {
            await blobs.UploadAsync(blobName, content, cancellationToken).ConfigureAwait(false);

            var resultType = await verdicts
                .WaitAsync(blobName, options.DefenderResultTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(resultType, NoThreatsFound, StringComparison.OrdinalIgnoreCase))
            {
                return ScanVerdict.Clean;
            }

            if (string.Equals(resultType, Malicious, StringComparison.OrdinalIgnoreCase))
            {
                // **検出したことはサーバ側にだけ残す。** 回答者へは出さない
                logger.LogWarning("添付から Defender for Storage がマルウェアを検出した");
                return ScanVerdict.Infected;
            }

            // Error / Not scanned（サイズ超過など）。**通してよい根拠にならない**
            throw new VirusScannerUnavailableException(
                $"Defender for Storage が判定を返さなかった: {resultType}");
        }
        catch (TimeoutException exception)
        {
            // **大きい・入れ子の多い blob は数十分かかり得る。** 待たずに拒否する
            throw new VirusScannerUnavailableException(
                "Defender for Storage の判定が時間内に届かなかった", exception);
        }
        catch (Exception exception) when (
            exception is not VirusScannerUnavailableException and not OperationCanceledException)
        {
            throw new VirusScannerUnavailableException(
                "DMZ のストレージへ添付を置けなかった", exception);
        }
        finally
        {
            // **中身を残さない。** 検出したものの調査は Defender 側のアラートと論理削除に任せる
            try
            {
                await blobs.DeleteAsync(blobName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 消せなくても回答の処理は続ける。**溜まり続けるので通知の対象**
                logger.LogError(exception, "DMZ に置いた添付を消せなかった: {BlobName}", blobName);
            }
        }
    }
}
