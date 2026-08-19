using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>アンケートに紐づく画像 1 枚（Issue #56）。</summary>
/// <param name="AssetId">識別子。**行は上書きしないので、差し替えると別の値になる。**</param>
/// <param name="ContentType">配信するときの型。**サーバが拡張子から決めた値。**</param>
/// <param name="Content">中身。</param>
public sealed record SurveyAsset(Guid AssetId, string ContentType, byte[] Content);

/// <summary>アンケートに紐づく画像を読み書きする。</summary>
/// <remarks>
/// <para>
/// **読み出しは必ずアンケートで絞る。** 識別子だけで引けるようにすると、
/// 識別子を推し当てるだけで**下書きのままのアンケートの画像**まで取り出せる。
/// </para>
/// <para>
/// **消す口は用意しない。** 差し替えても、
/// **公開済みの版がまだその画像を指していることがある**（<c>SurveyVersions</c> は不変）。
/// 消してよいかは版を全部見ないと決まらないので、ここでは判断しない。
/// </para>
/// </remarks>
public interface ISurveyAssetStore
{
    /// <summary>画像を足して、その識別子を返す。**既にある行は触らない。**</summary>
    Task<Guid> AddAsync(
        Guid surveyId,
        string contentType,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>画像を読む。**そのアンケートのものでなければ <c>null</c>。**</summary>
    Task<SurveyAsset?> FindAsync(
        Guid surveyId,
        Guid assetId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class SurveyAssetStore(IDbConnectionFactory connectionFactory) : ISurveyAssetStore
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SurveyAssets] "
            + "  ([AssetId], [SurveyId], [ContentType], [FileName], [ByteSize], "
            + "   [ContentBase64], [CreatedAt]) "
            + "VALUES (@AssetId, @SurveyId, @ContentType, @FileName, @ByteSize, "
            + "        @ContentBase64, @Now)",
            new
            {
                AssetId = assetId,
                SurveyId = surveyId,
                ContentType = contentType,
                FileName = fileName,
                ByteSize = (long)content.Length,
                ContentBase64 = Convert.ToBase64String(content),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return assetId;
    }

    public async Task<SurveyAsset?> FindAsync(
        Guid surveyId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **アンケートと組で絞る。** 識別子だけでは他のアンケートの画像が引ける
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT [AssetId], [ContentType], [ContentBase64] FROM [SurveyAssets] "
            + "WHERE [SurveyId] = @SurveyId AND [AssetId] = @AssetId",
            new { SurveyId = surveyId, AssetId = assetId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // **読めない中身は「無い」として扱う。** 例外にすると、
        // 1 枚の壊れでアンケートそのものが開けなくなる
        try
        {
            return new SurveyAsset(
                row.AssetId, row.ContentType, Convert.FromBase64String(row.ContentBase64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(connectionFactory.Provider, sql),
            parameters,
            transaction,
            cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
