using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>監視へ出す匿名な集計値を読む。</summary>
public interface IMonitoringStore
{
    /// <summary>公開中で、アーカイブされていないアンケートの件数を読む。</summary>
    Task<int> CountPublishedSurveysAsync(CancellationToken cancellationToken = default);
}

/// <summary>監視へ出す匿名な集計値を DB から読む。</summary>
public sealed class MonitoringStore(IDbConnectionFactory connectionFactory) : IMonitoringStore
{
    public async Task<int> CountPublishedSurveysAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            SqlDialect.Format(
                connectionFactory.Provider,
                "SELECT COUNT(*) FROM [Surveys] "
                + "WHERE [Status] = @PublishedStatus AND [ArchivedAt] IS NULL"),
            new { PublishedStatus = (int)SurveyStatus.Published },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
