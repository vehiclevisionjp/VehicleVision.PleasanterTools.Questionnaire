using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>アンケートの完全削除を 4 RDBMS で確かめる。</summary>
public class SurveyDeletionStoreTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task アーカイブ済みの関連行を残さず削除して監査ログは残す(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = CreateFactory(provider, connectionString);
        var surveyId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var archivedAt = DbTime.UtcNowTruncated();
        await SeedCompleteSurveyAsync(factory, surveyId, sourceId, archivedAt);

        var assets = new RecordingAssetStore();
        var result = await new SurveyDeletionStore(factory, assets)
            .DeleteAsync(surveyId, "完全削除の検証");

        Assert.Equal(SurveyDeletionStatus.Deleted, result.Status);
        Assert.Equal([surveyId], assets.DeletedSurveyIds);
        Assert.Equal(surveyId, result.Survey!.SurveyId);
        Assert.Equal($"public-{surveyId:N}", result.Survey.PublicId);
        Assert.Equal("完全削除の検証", result.Survey.Title);
        Assert.Equal(987L, result.Survey.PleasanterSiteId);
        Assert.Equal(1L, result.Survey.ResponseCount);
        Assert.Equal(archivedAt, result.Survey.ArchivedAt);

        await using var connection = factory.Create();
        await connection.OpenAsync();
        foreach (var table in new[]
        {
            "Surveys",
            "SurveyVersions",
            "ResponseTokens",
            "Responses",
            "ResponseEditTokens",
            "AssetTickets",
            "MailOutbox",
            "AdminNotifications",
            "SurveyAssets",
            "Pages",
            "Questions",
            "QuestionChoices",
            "ColumnAssignments",
            "AttachmentRejections",
        })
        {
            Assert.Equal(
                0L,
                await connection.ExecuteScalarAsync<long>(Sql(
                    provider,
                    $"SELECT COUNT(*) FROM [{table}] WHERE [SurveyId] = @SurveyId"),
                    new { SurveyId = surveyId }));
        }

        Assert.Equal(
            0L,
            await connection.ExecuteScalarAsync<long>(
                Sql(provider, "SELECT COUNT(*) FROM [AssignmentSources] WHERE [SourceId] = @SourceId"),
                new { SourceId = sourceId }));
        Assert.Equal(
            1L,
            await connection.ExecuteScalarAsync<long>(
                Sql(provider, "SELECT COUNT(*) FROM [AuditLogs] WHERE [TargetId] = @TargetId"),
                new { TargetId = surveyId.ToString() }));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task アーカイブ前または題名不一致なら何も削除しない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = CreateFactory(provider, connectionString);
        var surveys = new SurveyRepository(factory);
        var surveyId = Guid.NewGuid();
        await surveys.SaveAsync(Record(surveyId, archivedAt: null));
        var deletion = new SurveyDeletionStore(factory);

        Assert.Equal(
            SurveyDeletionStatus.NotArchived,
            (await deletion.DeleteAsync(surveyId, "完全削除の検証")).Status);

        var archived = (await surveys.FindBySurveyIdAsync(surveyId))! with
        {
            ArchivedAt = DbTime.UtcNowTruncated(),
        };
        await surveys.SaveAsync(archived);

        Assert.Equal(
            SurveyDeletionStatus.TitleMismatch,
            (await deletion.DeleteAsync(surveyId, "違う題名")).Status);
        Assert.NotNull(await surveys.FindBySurveyIdAsync(surveyId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 送信待ちの回答があれば何も削除しない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = CreateFactory(provider, connectionString);
        var surveyId = Guid.NewGuid();
        await new SurveyRepository(factory).SaveAsync(
            Record(surveyId, DbTime.UtcNowTruncated()));
        await InsertPendingResponseAsync(factory, surveyId);

        var result = await new SurveyDeletionStore(factory)
            .DeleteAsync(surveyId, "完全削除の検証");

        Assert.Equal(SurveyDeletionStatus.PendingDelivery, result.Status);
        Assert.NotNull(await new SurveyRepository(factory).FindBySurveyIdAsync(surveyId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 送信待ちのメールがあれば何も削除しない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var factory = CreateFactory(provider, connectionString);
        var surveyId = Guid.NewGuid();
        await new SurveyRepository(factory).SaveAsync(
            Record(surveyId, DbTime.UtcNowTruncated()));
        await new MailOutbox(factory).EnqueueAsync(Guid.NewGuid(), 0, surveyId, "encrypted");

        var result = await new SurveyDeletionStore(factory)
            .DeleteAsync(surveyId, "完全削除の検証");

        Assert.Equal(SurveyDeletionStatus.PendingDelivery, result.Status);
        Assert.NotNull(await new SurveyRepository(factory).FindBySurveyIdAsync(surveyId));
    }

    private static DbConnectionFactory CreateFactory(
        DatabaseProvider provider,
        string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        return new DbConnectionFactory(provider, connectionString);
    }

    private sealed class RecordingAssetStore : ISurveyAssetStore
    {
        public List<Guid> DeletedSurveyIds { get; } = [];

        public Task<Guid> AddAsync(
            Guid surveyId,
            string contentType,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SurveyAsset?> FindAsync(
            Guid surveyId,
            Guid assetId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Guid?> TryAddContentAsync(
            Guid surveyId,
            string contentType,
            string fileName,
            byte[] content,
            int maximumCount,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSurveyAsync(
            Guid surveyId,
            CancellationToken cancellationToken = default)
        {
            DeletedSurveyIds.Add(surveyId);
            return Task.CompletedTask;
        }
    }

    private static SurveyRecord Record(Guid surveyId, DateTime? archivedAt) =>
        new(
            surveyId,
            $"public-{surveyId:N}",
            "完全削除の検証",
            PleasanterSiteId: 987,
            ResponseJsonColumn: null,
            Status: (int)SurveyStatus.Suspended,
            PublishedVersion: 1,
            ArchivedAt: archivedAt);

    private static async Task SeedCompleteSurveyAsync(
        DbConnectionFactory factory,
        Guid surveyId,
        Guid sourceId,
        DateTime archivedAt)
    {
        await new SurveyRepository(factory).SaveAsync(Record(surveyId, archivedAt));
        var now = DbTime.UtcNowTruncated();
        var assignmentId = Guid.NewGuid();
        var responseToken = $"response-{Guid.NewGuid():N}";

        await using var connection = factory.Create();
        await connection.OpenAsync();
        var commands = new[]
        {
            (
                "INSERT INTO [SurveyVersions] "
                    + "([SurveyId], [Version], [DefinitionJson], [MappingJson], [PublishedAt]) "
                    + "VALUES (@SurveyId, 1, @Json, @Json, @Now)",
                (object)new { SurveyId = surveyId, Json = "{}", Now = now }),
            (
                "INSERT INTO [ResponseTokens] "
                    + "([ResponseToken], [SurveyId], [PleasanterReferenceId], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@ResponseToken, @SurveyId, 1, @Now, @Now)",
                new { ResponseToken = responseToken, SurveyId = surveyId, Now = now }),
            (
                "INSERT INTO [Responses] "
                    + "([ResponseToken], [SurveyId], [SurveyVersion], [PayloadJson], [Status], "
                    + " [RetryCount], [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@ResponseToken, @SurveyId, 1, @Json, @Status, 0, @Now, @Now, @Now)",
                new
                {
                    ResponseToken = responseToken,
                    SurveyId = surveyId,
                    Json = "{}",
                    Status = (int)ResponseStatus.DeadLetter,
                    Now = now,
                }),
            (
                "INSERT INTO [ResponseEditTokens] "
                    + "([EditTokenHash], [ResponseToken], [SurveyId], [ExpiresAt], [CreatedAt]) "
                    + "VALUES (@Hash, @ResponseToken, @SurveyId, @Now, @Now)",
                new
                {
                    Hash = $"hash-{Guid.NewGuid():N}",
                    ResponseToken = responseToken,
                    SurveyId = surveyId,
                    Now = now,
                }),
            (
                "INSERT INTO [AssetTickets] "
                    + "([TicketHash], [ResponseToken], [SurveyId], [ExpiresAt], [CreatedAt]) "
                    + "VALUES (@Hash, @ResponseToken, @SurveyId, @Now, @Now)",
                new
                {
                    Hash = $"hash-{Guid.NewGuid():N}",
                    ResponseToken = responseToken,
                    SurveyId = surveyId,
                    Now = now,
                }),
            (
                "INSERT INTO [MailOutbox] "
                    + "([MailId], [Kind], [SurveyId], [PayloadProtected], [Status], [RetryCount], "
                    + " [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@Id, 0, @SurveyId, @Payload, @Status, 0, @Now, @Now, @Now)",
                new
                {
                    Id = Guid.NewGuid(),
                    SurveyId = surveyId,
                    Payload = "encrypted",
                    Status = (int)MailStatus.DeadLetter,
                    Now = now,
                }),
            (
                "INSERT INTO [AdminNotifications] "
                    + "([AdminNotificationId], [Kind], [SurveyId], [Count], "
                    + " [FirstOccurredAt], [LastOccurredAt]) "
                    + "VALUES (@Id, 0, @SurveyId, 1, @Now, @Now)",
                new { Id = Guid.NewGuid(), SurveyId = surveyId, Now = now }),
            (
                "INSERT INTO [SurveyAssets] "
                    + "([AssetId], [SurveyId], [ContentType], [FileName], [ByteSize], "
                    + " [ContentBase64], [CreatedAt]) "
                    + "VALUES (@Id, @SurveyId, @ContentType, @FileName, 1, @Content, @Now)",
                new
                {
                    Id = Guid.NewGuid(),
                    SurveyId = surveyId,
                    ContentType = "image/png",
                    FileName = "header.png",
                    Content = "AA==",
                    Now = now,
                }),
            (
                "INSERT INTO [Pages] "
                    + "([SurveyId], [PageId], [SortOrder]) VALUES (@SurveyId, @PageId, 0)",
                new { SurveyId = surveyId, PageId = "page-1" }),
            (
                "INSERT INTO [Questions] "
                    + "([SurveyId], [QuestionId], [PageId], [SortOrder], [QuestionType], "
                    + " [TitleJson], [IsRequired]) "
                    + "VALUES (@SurveyId, @QuestionId, @PageId, 0, 0, @Title, @Required)",
                new
                {
                    SurveyId = surveyId,
                    QuestionId = "question-1",
                    PageId = "page-1",
                    Title = "{}",
                    Required = false,
                }),
            (
                "INSERT INTO [QuestionChoices] "
                    + "([ChoiceId], [SurveyId], [QuestionId], [SortOrder], [Value], "
                    + " [LabelJson], [IsOther]) "
                    + "VALUES (@Id, @SurveyId, @QuestionId, 0, @Value, @Label, @IsOther)",
                new
                {
                    Id = Guid.NewGuid(),
                    SurveyId = surveyId,
                    QuestionId = "question-1",
                    Value = "one",
                    Label = "{}",
                    IsOther = false,
                }),
            (
                "INSERT INTO [ColumnAssignments] "
                    + "([AssignmentId], [SurveyId], [TargetColumn], [SortOrder]) "
                    + "VALUES (@AssignmentId, @SurveyId, @TargetColumn, 0)",
                new { AssignmentId = assignmentId, SurveyId = surveyId, TargetColumn = "ClassA" }),
            (
                "INSERT INTO [AssignmentSources] "
                    + "([SourceId], [AssignmentId], [QuestionId], [Port], [SortOrder]) "
                    + "VALUES (@SourceId, @AssignmentId, @QuestionId, 0, 0)",
                new
                {
                    SourceId = sourceId,
                    AssignmentId = assignmentId,
                    QuestionId = "question-1",
                }),
            (
                "INSERT INTO [AttachmentRejections] "
                    + "([AttachmentRejectionId], [OccurredAt], [SurveyId], [Reason], [FileCount]) "
                    + "VALUES (@Id, @Now, @SurveyId, 0, 1)",
                new { Id = Guid.NewGuid(), Now = now, SurveyId = surveyId }),
            (
                "INSERT INTO [AuditLogs] "
                    + "([AuditLogId], [OccurredAt], [Action], [TargetType], [TargetId]) "
                    + "VALUES (@Id, @Now, @Action, @TargetType, @TargetId)",
                new
                {
                    Id = Guid.NewGuid(),
                    Now = now,
                    Action = "POST /api/admin/surveys/{surveyId}/archive",
                    TargetType = "Survey",
                    TargetId = surveyId.ToString(),
                }),
        };

        foreach (var (sql, parameters) in commands)
        {
            await connection.ExecuteAsync(Sql(factory.Provider, sql), parameters);
        }
    }

    private static async Task InsertPendingResponseAsync(
        DbConnectionFactory factory,
        Guid surveyId)
    {
        var now = DbTime.UtcNowTruncated();
        var responseToken = $"response-{Guid.NewGuid():N}";
        await using var connection = factory.Create();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            Sql(
                factory.Provider,
                "INSERT INTO [Responses] "
                    + "([ResponseToken], [SurveyId], [SurveyVersion], [PayloadJson], [Status], "
                    + " [RetryCount], [NextAttemptAt], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@ResponseToken, @SurveyId, 1, @Json, @Status, 0, @Now, @Now, @Now)"),
            new
            {
                ResponseToken = responseToken,
                SurveyId = surveyId,
                Json = "{}",
                Status = (int)ResponseStatus.Pending,
                Now = now,
            });
    }

    private static string Sql(DatabaseProvider provider, string sql) =>
        SqlDialect.Format(provider, sql);
}
