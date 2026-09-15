using Azure.Storage.Blobs;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>未検査の添付を置く場所。</summary>
/// <remarks>
/// **DMZ（untrusted 用）のストレージアカウントを分けること**
/// （<c>_documents/添付ファイル検査-運用手順書.md</c> 4 章）。
/// 検査が済んだものだけを本来の置き場へ移す構成が推奨されている。
/// </remarks>
public interface IDmzBlobStore
{
    /// <summary>置く。</summary>
    /// <remarks>
    /// **置いた直後にメタデータを更新しないこと。** on-upload スキャンが失敗し得る。
    /// </remarks>
    Task UploadAsync(string blobName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>消す。**中身を残さない**（個人情報を含み得る）。</summary>
    Task DeleteAsync(string blobName, CancellationToken cancellationToken);
}

/// <summary>Azure Blob Storage を使った実装。</summary>
public sealed class AzureDmzBlobStore : IDmzBlobStore
{
    private readonly BlobContainerClient _container;

    public AzureDmzBlobStore(VirusScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // **資格情報はサーバ側だけで持つ。** SAS 付きの URL か接続文字列を設定から受け取る
        _container = options.DefenderContainerUrl is { Length: > 0 } url
            ? new BlobContainerClient(new Uri(url))
            : new BlobContainerClient(
                options.DefenderConnectionString
                    ?? throw new InvalidOperationException(
                        "QUESTIONNAIRE_VIRUSSCAN_DEFENDER_CONNECTIONSTRING か "
                        + "QUESTIONNAIRE_VIRUSSCAN_DEFENDER_CONTAINERURL が設定されていない"),
                options.DefenderContainer);
    }

    public async Task UploadAsync(
        string blobName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await _container.GetBlobClient(blobName)
            .UploadAsync(stream, overwrite: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
        await _container.GetBlobClient(blobName)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
}
