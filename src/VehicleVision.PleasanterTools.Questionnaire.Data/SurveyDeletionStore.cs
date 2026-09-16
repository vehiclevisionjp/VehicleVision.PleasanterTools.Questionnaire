using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>アンケートの完全削除で返す状態。</summary>
public enum SurveyDeletionStatus
{
    Deleted,
    NotFound,
    NotArchived,
    Template,
    TitleMismatch,
    PendingDelivery,
}

/// <summary>削除前に監査ログへ退避するアンケートの値。</summary>
public sealed record DeletedSurvey(
    Guid SurveyId,
    string PublicId,
    string Title,
    long PleasanterSiteId,
    long ResponseCount,
    DateTime ArchivedAt);

/// <summary>アンケートの完全削除結果。</summary>
public sealed record SurveyDeletionResult(
    SurveyDeletionStatus Status,
    DeletedSurvey? Survey = null);

/// <summary>アーカイブ済みアンケートと本アプリ内の関連行を完全に削除する。</summary>
public interface ISurveyDeletionStore
{
    /// <summary>
    /// 題名と状態を確認し、関連行を 1 つのトランザクションで削除する。
    /// </summary>
    Task<SurveyDeletionResult> DeleteAsync(
        Guid surveyId,
        string? expectedTitle,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SurveyDeletionStore(
    IDbConnectionFactory connectionFactory,
    ISurveyAssetStore? assetStore = null) : ISurveyDeletionStore
{
    private sealed class SurveyRow
    {
        public Guid SurveyId { get; set; }

        public string PublicId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public long PleasanterSiteId { get; set; }

        public bool IsTemplate { get; set; }

        public DateTime? ArchivedAt { get; set; }

        public long ResponseCount { get; set; }
    }

    public async Task<SurveyDeletionResult> DeleteAsync(
        Guid surveyId,
        string? expectedTitle,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var survey = await connection.QuerySingleOrDefaultAsync<SurveyRow>(Command(
            "SELECT s.[SurveyId], s.[PublicId], s.[Title], s.[PleasanterSiteId], "
            + "       s.[IsTemplate], s.[ArchivedAt], "
            + "       (SELECT COUNT(*) FROM [ResponseTokens] t "
            + "        WHERE t.[SurveyId] = s.[SurveyId]) AS [ResponseCount] "
            + "FROM [Surveys] s WHERE s.[SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken)).ConfigureAwait(false);

        if (survey is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.NotFound);
        }

        if (survey.IsTemplate)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.Template);
        }

        if (survey.ArchivedAt is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.NotArchived);
        }

        if (!string.Equals(survey.Title, expectedTitle, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.TitleMismatch);
        }

        var pendingResponses = await connection.ExecuteScalarAsync<long>(Command(
            "SELECT COUNT(*) FROM [Responses] "
            + "WHERE [SurveyId] = @SurveyId AND [Status] <> @DeadLetterStatus",
            new
            {
                SurveyId = surveyId,
                DeadLetterStatus = (int)ResponseStatus.DeadLetter,
            },
            transaction,
            cancellationToken)).ConfigureAwait(false);
        var pendingMail = await connection.ExecuteScalarAsync<long>(Command(
            "SELECT COUNT(*) FROM [MailOutbox] "
            + "WHERE [SurveyId] = @SurveyId AND [Status] <> @DeadLetterStatus",
            new
            {
                SurveyId = surveyId,
                DeadLetterStatus = (int)MailStatus.DeadLetter,
            },
            transaction,
            cancellationToken)).ConfigureAwait(false);

        if (pendingResponses > 0 || pendingMail > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.PendingDelivery);
        }

        // **DB の行より先に消す。** 外部保存先の削除に失敗したまま DB だけ消すと、
        // 次の試行で対象を特定できず、実体が恒久的に残るため。
        if (assetStore is not null)
        {
            await assetStore.DeleteSurveyAsync(surveyId, cancellationToken).ConfigureAwait(false);
        }

        await DeleteAsync(
            connection,
            transaction,
            "DELETE FROM [AssignmentSources] WHERE [AssignmentId] IN "
                + "(SELECT [AssignmentId] FROM [ColumnAssignments] WHERE [SurveyId] = @SurveyId)",
            surveyId,
            cancellationToken).ConfigureAwait(false);

        foreach (var table in new[]
        {
            "QuestionChoices",
            "Questions",
            "Pages",
            "ColumnAssignments",
            "AttachmentRejections",
            "SurveyAssets",
            "AdminNotifications",
            "AssetTickets",
            "ResponseEditTokens",
            "MailOutbox",
            "Responses",
            "ResponseTokens",
            "SurveyVersions",
        })
        {
            await DeleteAsync(
                connection,
                transaction,
                $"DELETE FROM [{table}] WHERE [SurveyId] = @SurveyId",
                surveyId,
                cancellationToken).ConfigureAwait(false);
        }

        var deleted = await connection.ExecuteAsync(Command(
            "DELETE FROM [Surveys] WHERE [SurveyId] = @SurveyId "
                + "AND [ArchivedAt] IS NOT NULL AND [IsTemplate] = @IsTemplate",
            new { SurveyId = surveyId, IsTemplate = false },
            transaction,
            cancellationToken)).ConfigureAwait(false);
        if (deleted != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(SurveyDeletionStatus.NotArchived);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            SurveyDeletionStatus.Deleted,
            new DeletedSurvey(
                survey.SurveyId,
                survey.PublicId,
                survey.Title,
                survey.PleasanterSiteId,
                survey.ResponseCount,
                survey.ArchivedAt.Value));
    }

    private string Sql(string sql) => SqlDialect.Format(connectionFactory.Provider, sql);

    private CommandDefinition Command(
        string sql,
        object parameters,
        DbTransaction transaction,
        CancellationToken cancellationToken) =>
        new(Sql(sql), parameters, transaction, cancellationToken: cancellationToken);

    private async Task DeleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Guid surveyId,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(Command(
            sql,
            new { SurveyId = surveyId },
            transaction,
            cancellationToken)).ConfigureAwait(false);
}
