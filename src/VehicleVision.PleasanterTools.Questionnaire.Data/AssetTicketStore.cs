using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>配布資産の引換券を引き換えた結果。</summary>
public sealed record AssetTicketGrant(Guid SurveyId, string ResponseToken, DateTime ExpiresAtUtc);

/// <summary>回答後の配布資産に使う引換券の読み書き（Issue #318）。</summary>
public interface IAssetTicketStore
{
    Task SaveAsync(
        string ticketHash,
        string responseToken,
        Guid surveyId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>期限内なら返す。**行は消さず、何度でも使える。**</summary>
    Task<AssetTicketGrant?> RedeemAsync(
        string ticketHash,
        Guid surveyId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> RevokeBySurveyAsync(Guid surveyId, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(DateTime threshold, CancellationToken cancellationToken = default);
}

public sealed class AssetTicketStore(IDbConnectionFactory connectionFactory) : IAssetTicketStore
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task SaveAsync(
        string ticketHash,
        string responseToken,
        Guid surveyId,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseToken);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AssetTickets] "
            + "([TicketHash], [ResponseToken], [SurveyId], [ExpiresAt], [CreatedAt]) "
            + "VALUES (@TicketHash, @ResponseToken, @SurveyId, @ExpiresAt, @Now)",
            new
            {
                TicketHash = ticketHash,
                ResponseToken = responseToken,
                SurveyId = surveyId,
                ExpiresAt = DbTime.ForDb(expiresAtUtc),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed record RedeemedRow(Guid SurveyId, string ResponseToken, DateTime ExpiresAt);

    public async Task<AssetTicketGrant?> RedeemAsync(
        string ticketHash,
        Guid surveyId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticketHash))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<RedeemedRow>(Sql(
            "SELECT [SurveyId], [ResponseToken], [ExpiresAt] FROM [AssetTickets] "
            + "WHERE [TicketHash] = @TicketHash AND [SurveyId] = @SurveyId AND [ExpiresAt] > @Now",
            new { TicketHash = ticketHash, SurveyId = surveyId, Now = DbTime.ForDb(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new AssetTicketGrant(row.SurveyId, row.ResponseToken, row.ExpiresAt);
    }

    public async Task<int> RevokeBySurveyAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AssetTickets] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteExpiredAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AssetTickets] WHERE [ExpiresAt] < @Threshold",
            new { Threshold = DbTime.ForDb(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
