using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>GUID をキーに資産の実体を読み書きする。</summary>
internal interface IAssetObjectStore
{
    Task PutAsync(Guid key, byte[] content, CancellationToken cancellationToken);

    Task<byte[]?> FindAsync(Guid key, CancellationToken cancellationToken);

    Task DeleteAsync(Guid key, CancellationToken cancellationToken);
}

/// <summary>メタデータを DB、実体を外部の保存先へ置く。</summary>
internal sealed class ExternalSurveyAssetStore(
    IDbConnectionFactory connectionFactory,
    IAssetObjectStore objects) : ISurveyAssetStore
{
    private sealed record Row(Guid AssetId, string ContentType, string ContentBase64);

    public async Task<Guid> AddAsync(
        Guid surveyId,
        string contentType,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var assetId = Guid.NewGuid();
        await objects.PutAsync(assetId, content, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [SurveyAssets] "
                + "  ([AssetId], [SurveyId], [ContentType], [FileName], [ByteSize], "
                + "   [ContentBase64], [CreatedAt]) "
                + "VALUES (@AssetId, @SurveyId, @ContentType, @FileName, @ByteSize, "
                + "        @StorageKey, @Now)",
                new
                {
                    AssetId = assetId,
                    SurveyId = surveyId,
                    ContentType = contentType,
                    FileName = fileName,
                    ByteSize = (long)content.Length,
                    StorageKey = assetId.ToString("N"),
                    Now = DbTime.UtcNowTruncated(),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            // **要求の中断後でも片付ける。** 同じキャンセル済みトークンを渡すと、
            // DB に行が無い実体だけが残るため。
            await objects.DeleteAsync(assetId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return assetId;
    }

    public async Task<SurveyAsset?> FindAsync(
        Guid surveyId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT [AssetId], [ContentType], [ContentBase64] FROM [SurveyAssets] "
            + "WHERE [SurveyId] = @SurveyId AND [AssetId] = @AssetId",
            new { SurveyId = surveyId, AssetId = assetId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        if (Guid.TryParseExact(row.ContentBase64, "N", out var storageKey))
        {
            var content = await objects.FindAsync(storageKey, cancellationToken)
                .ConfigureAwait(false);
            return content is null ? null : new(row.AssetId, row.ContentType, content);
        }

        // **DB 保存から切り替えた直後も既存画像を配信する。**
        // 移行前の行には保存キーではなく Base64 の実体が残っているため。
        try
        {
            return new(row.AssetId, row.ContentType, Convert.FromBase64String(row.ContentBase64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task DeleteSurveyAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var values = await connection.QueryAsync<string>(Sql(
            "SELECT [ContentBase64] FROM [SurveyAssets] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(value, "N", out var storageKey))
            {
                continue;
            }

            // **複製したアンケートは同じ実体を指す。**
            // 最後の参照でない限り消すと、残ったアンケートの画像が欠ける。
            var references = await connection.ExecuteScalarAsync<long>(Sql(
                "SELECT COUNT(*) FROM [SurveyAssets] WHERE [ContentBase64] = @StorageKey",
                new { StorageKey = value },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (references == 1)
            {
                await objects.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(
            SqlDialect.Format(connectionFactory.Provider, sql),
            parameters,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
