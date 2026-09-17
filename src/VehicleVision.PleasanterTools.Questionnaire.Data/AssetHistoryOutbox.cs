using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

public enum AssetHistoryEventType
{
    Revisit = 1,
    Download = 2,
}

public enum AssetHistoryStatus
{
    Pending = 0,
    Sending = 1,
    DeadLetter = 2,
}

public sealed record PendingAssetHistory(
    Guid EventId,
    Guid SurveyId,
    int SurveyVersion,
    string ResponseToken,
    int EventType,
    Guid? AssetId,
    string? AssetFileName,
    DateTime OccurredAt,
    int RetryCount);

public interface IAssetHistoryOutbox
{
    Task EnqueueAsync(
        Guid surveyId,
        int surveyVersion,
        string responseToken,
        AssetHistoryEventType eventType,
        Guid? assetId,
        string? assetFileName,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default);

    Task<PendingAssetHistory?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task WaitAsync(Guid eventId, DateTime nextAttemptAtUtc, CancellationToken cancellationToken = default);
    Task RescheduleAsync(Guid eventId, DateTime nextAttemptAtUtc, string error, CancellationToken cancellationToken = default);
    Task DeadLetterAsync(Guid eventId, string error, CancellationToken cancellationToken = default);
    Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default);
}

/// <summary>出来事を複数件保持できる、配布資料履歴専用の送信待ち。</summary>
public sealed class AssetHistoryOutbox(IDbConnectionFactory connectionFactory) : IAssetHistoryOutbox
{
    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task EnqueueAsync(
        Guid surveyId,
        int surveyVersion,
        string responseToken,
        AssetHistoryEventType eventType,
        Guid? assetId,
        string? assetFileName,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseToken);
        var now = DbTime.UtcNowTruncated();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AssetHistoryOutbox] "
            + "([EventId], [SurveyId], [SurveyVersion], [ResponseToken], [EventType], [AssetId], "
            + "[AssetFileName], [OccurredAt], [Status], [RetryCount], [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
            + "VALUES (@EventId, @SurveyId, @SurveyVersion, @ResponseToken, @EventType, @AssetId, "
            + "@AssetFileName, @OccurredAt, @Status, 0, @Now, @Now, @Now)",
            new
            {
                EventId = Guid.NewGuid(),
                SurveyId = surveyId,
                SurveyVersion = surveyVersion,
                ResponseToken = responseToken,
                EventType = (int)eventType,
                AssetId = assetId,
                AssetFileName = assetFileName,
                OccurredAt = DbTime.ForDb(occurredAtUtc),
                Status = (int)AssetHistoryStatus.Pending,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PendingAssetHistory?> ClaimAsync(
        string lockedBy,
        TimeSpan lockDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();
        var owner = lockedBy.Length > 24 ? lockedBy[..24] : lockedBy;
        var parameters = new
        {
            SendingStatus = (int)AssetHistoryStatus.Sending,
            PendingStatus = (int)AssetHistoryStatus.Pending,
            LockedBy = $"{owner}:{Guid.NewGuid():N}",
            LockedUntil = now.Add(lockDuration),
            Now = now,
        };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (SqlDialect.SupportsReturning(Provider))
        {
            return await connection.QueryFirstOrDefaultAsync<PendingAssetHistory>(Sql(
                SqlDialect.ClaimPendingAssetHistory(Provider), parameters, cancellationToken))
                .ConfigureAwait(false);
        }

        var affected = await connection.ExecuteAsync(Sql(
            SqlDialect.ClaimPendingAssetHistory(Provider), parameters, cancellationToken))
            .ConfigureAwait(false);
        return affected == 0
            ? null
            : await connection.QueryFirstOrDefaultAsync<PendingAssetHistory>(Sql(
                SqlDialect.ReadClaimedAssetHistoryForMySql, parameters, cancellationToken))
                .ConfigureAwait(false);
    }

    public Task CompleteAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM [AssetHistoryOutbox] WHERE [EventId] = @EventId", new { EventId = eventId }, cancellationToken);

    public Task WaitAsync(
        Guid eventId,
        DateTime nextAttemptAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AssetHistoryOutbox] SET [Status] = @Status, [NextAttemptAt] = @NextAttemptAt, "
            + "[LockedBy] = NULL, [LockedUntil] = NULL, [UpdatedAt] = @Now WHERE [EventId] = @EventId",
            new
            {
                EventId = eventId,
                Status = (int)AssetHistoryStatus.Pending,
                NextAttemptAt = DbTime.ForDb(nextAttemptAtUtc),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken);

    public Task RescheduleAsync(
        Guid eventId,
        DateTime nextAttemptAtUtc,
        string error,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AssetHistoryOutbox] SET [Status] = @Status, [RetryCount] = [RetryCount] + 1, "
            + "[NextAttemptAt] = @NextAttemptAt, [LastError] = @Error, [LockedBy] = NULL, "
            + "[LockedUntil] = NULL, [UpdatedAt] = @Now WHERE [EventId] = @EventId",
            new
            {
                EventId = eventId,
                Status = (int)AssetHistoryStatus.Pending,
                NextAttemptAt = DbTime.ForDb(nextAttemptAtUtc),
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken);

    public Task DeadLetterAsync(Guid eventId, string error, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "UPDATE [AssetHistoryOutbox] SET [Status] = @Status, [LastError] = @Error, "
            + "[LockedBy] = NULL, [LockedUntil] = NULL, [UpdatedAt] = @Now WHERE [EventId] = @EventId",
            new
            {
                EventId = eventId,
                Status = (int)AssetHistoryStatus.DeadLetter,
                Error = Truncate(error),
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken);

    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [AssetHistoryOutbox] SET [Status] = @Pending, [LockedBy] = NULL, "
            + "[LockedUntil] = NULL, [UpdatedAt] = @Now "
            + "WHERE [Status] = @Sending AND [LockedUntil] < @Now",
            new
            {
                Pending = (int)AssetHistoryStatus.Pending,
                Sending = (int)AssetHistoryStatus.Sending,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken)).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(sql, parameters, cancellationToken)).ConfigureAwait(false);
    }

    private static string Truncate(string value) => value.Length <= 1024 ? value : value[..1024];

    private CommandDefinition Sql(string sql, object? parameters, CancellationToken cancellationToken) =>
        new(SqlDialect.Format(Provider, sql), parameters, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
