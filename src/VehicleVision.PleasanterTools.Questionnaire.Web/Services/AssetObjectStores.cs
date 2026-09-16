using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>指定ディレクトリへ資産を置く。</summary>
internal sealed class PathAssetObjectStore : IAssetObjectStore
{
    private readonly string root;

    public PathAssetObjectStore(string path)
    {
        root = Path.GetFullPath(path);
        Directory.CreateDirectory(root);
    }

    public async Task PutAsync(
        Guid key,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var destination = FilePath(key);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task<byte[]?> FindAsync(Guid key, CancellationToken cancellationToken)
    {
        var path = FilePath(key);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public Task DeleteAsync(Guid key, CancellationToken cancellationToken)
    {
        File.Delete(FilePath(key));
        return Task.CompletedTask;
    }

    private string FilePath(Guid key) => Path.Combine(root, key.ToString("N"));
}

/// <summary>Azure Blob Storage の非公開コンテナーへ資産を置く。</summary>
internal sealed class AzureBlobAssetObjectStore(BlobContainerClient container) : IAssetObjectStore
{
    public async Task PutAsync(
        Guid key,
        byte[] content,
        CancellationToken cancellationToken) =>
        await container.GetBlobClient(Name(key))
            .UploadAsync(BinaryData.FromBytes(content), overwrite: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<byte[]?> FindAsync(Guid key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(Name(key))
                .DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid key, CancellationToken cancellationToken) =>
        _ = await container.GetBlobClient(Name(key))
            .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    private static string Name(Guid key) => key.ToString("N");
}

/// <summary>S3 または S3 互換の非公開バケットへ資産を置く。</summary>
internal sealed class S3AssetObjectStore(IAmazonS3 client, string bucket) : IAssetObjectStore
{
    public async Task PutAsync(
        Guid key,
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = Name(key),
            InputStream = stream,
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> FindAsync(Guid key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(
                bucket, Name(key), cancellationToken).ConfigureAwait(false);
            using var content = new MemoryStream();
            await response.ResponseStream.CopyToAsync(content, cancellationToken)
                .ConfigureAwait(false);
            return content.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid key, CancellationToken cancellationToken) =>
        _ = await client.DeleteObjectAsync(bucket, Name(key), cancellationToken)
            .ConfigureAwait(false);

    private static string Name(Guid key) => key.ToString("N");
}
