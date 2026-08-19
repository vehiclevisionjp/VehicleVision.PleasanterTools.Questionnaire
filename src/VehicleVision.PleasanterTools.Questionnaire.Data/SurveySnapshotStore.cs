using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>公開済みのスナップショットを DB から読む。</summary>
/// <remarks>
/// **<c>SurveyVersions</c> は消さない。** 過去の回答を解釈するために要る
/// （<c>_documents/データモデル設計.md</c> 6 章）。
/// </remarks>
public sealed class SurveySnapshotStore(IDbConnectionFactory connectionFactory) : ISurveySnapshotStore
{
    private sealed record Row(
        string DefinitionJson,
        string MappingJson,
        long PleasanterSiteId,
        string? ResponseJsonColumn);

    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む。**</summary>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(connectionFactory.Provider, sql),
            parameters,
            cancellationToken: cancellationToken);

    public async Task<SurveySnapshot?> FindAsync(
        Guid surveyId,
        int version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT v.[DefinitionJson], v.[MappingJson], "
            + "       s.[PleasanterSiteId], s.[ResponseJsonColumn] "
            + "FROM [SurveyVersions] v "
            + "JOIN [Surveys] s ON s.[SurveyId] = v.[SurveyId] "
            + "WHERE v.[SurveyId] = @SurveyId AND v.[Version] = @Version",
            new { SurveyId = surveyId, Version = version },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var definition = SurveyJson.Deserialize<SurveyDefinition>(row.DefinitionJson);
        var mapping = SurveyJson.Deserialize<MappingDefinition>(row.MappingJson);

        // **読めない版は「無い」として扱う。** 送信ワーカーがデッドレターへ回す
        return definition is null || mapping is null
            ? null
            : new SurveySnapshot(definition, mapping, row.PleasanterSiteId, row.ResponseJsonColumn);
    }
}

/// <summary>アンケートと公開済みの版を書き込む。</summary>
public interface ISurveyRepository
{
    /// <summary>アンケートを作る、または更新する。</summary>
    Task SaveAsync(SurveyRecord survey, CancellationToken cancellationToken = default);

    /// <summary>公開して版を固める。**不変なので、同じ版を上書きしない。**</summary>
    Task PublishAsync(
        Guid surveyId,
        int version,
        SurveyDefinition definition,
        MappingDefinition mapping,
        Guid? publishedBy,
        CancellationToken cancellationToken = default);

    /// <summary>公開用 ID からアンケートを引く。回答画面が使う。</summary>
    Task<SurveyRecord?> FindByPublicIdAsync(
        string publicId,
        CancellationToken cancellationToken = default);

    /// <summary>内部 ID からアンケートを引く。管理画面が使う。</summary>
    Task<SurveyRecord?> FindBySurveyIdAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default);
}

/// <summary>アンケートの 1 行。</summary>
/// <param name="IsTemplate">
/// テンプレートか（Issue #58）。
///
/// **テンプレートは Pleasanter のサイトを持たない**ので、
/// <see cref="PleasanterSiteId"/> は 0 が入っている。公開も停止もできない
/// （<c>AdminSurveyEndpoints</c> で断る）。
/// </param>
public sealed record SurveyRecord(
    Guid SurveyId,
    string PublicId,
    string Title,
    long PleasanterSiteId,
    string? ResponseJsonColumn,
    int Status,
    int? PublishedVersion,
    DateTime? AcceptFrom = null,
    DateTime? AcceptTo = null,
    int? ResponseLimit = null,
    bool IsTemplate = false);

/// <summary>アンケートの状態。</summary>
public enum SurveyStatus
{
    /// <summary>下書き。**公開していないので回答できない。**</summary>
    Draft = 0,

    /// <summary>公開中。</summary>
    Published = 1,

    /// <summary>停止中。理由は <c>SuspendedReason</c>。</summary>
    Suspended = 2,
}

/// <summary>Dapper を使った実装。</summary>
public sealed class SurveyRepository(IDbConnectionFactory connectionFactory) : ISurveyRepository
{
    /// <remarks>
    /// **<c>IsTemplate</c> は書かない**（Issue #58）。
    /// テンプレートかどうかは作るときに決まるもので、
    /// ここで書くと、テンプレートの行を読んで書き戻した拍子に旗が落ちる。
    /// テンプレートを作るのは <see cref="ISurveyDraftStore.SaveAsTemplateAsync"/> だけ。
    /// </remarks>
    public async Task SaveAsync(SurveyRecord survey, CancellationToken cancellationToken = default)
    {
        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(Sql(
            "UPDATE [Surveys] SET "
            + "  [PublicId] = @PublicId, [Title] = @Title, "
            + "  [PleasanterSiteId] = @PleasanterSiteId, "
            + "  [ResponseJsonColumn] = @ResponseJsonColumn, "
            + "  [Status] = @Status, [PublishedVersion] = @PublishedVersion, "
            + "  [AcceptFrom] = @AcceptFrom, [AcceptTo] = @AcceptTo, "
            + "  [ResponseLimit] = @ResponseLimit, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId",
            new
            {
                survey.SurveyId,
                survey.PublicId,
                survey.Title,
                survey.PleasanterSiteId,
                survey.ResponseJsonColumn,
                survey.Status,
                survey.PublishedVersion,
                survey.AcceptFrom,
                survey.AcceptTo,
                survey.ResponseLimit,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (updated > 0)
        {
            return;
        }

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [Surveys] "
            + "  ([SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "   [ResponseJsonColumn], [Status], [PublishedVersion], "
            + "   [AcceptFrom], [AcceptTo], [ResponseLimit], "
            + "   [CreatedAt], [UpdatedAt]) "
            + "VALUES (@SurveyId, @PublicId, @Title, @PleasanterSiteId, "
            + "        @ResponseJsonColumn, @Status, @PublishedVersion, "
            + "        @AcceptFrom, @AcceptTo, @ResponseLimit, @Now, @Now)",
            new
            {
                survey.SurveyId,
                survey.PublicId,
                survey.Title,
                survey.PleasanterSiteId,
                survey.ResponseJsonColumn,
                survey.Status,
                survey.PublishedVersion,
                survey.AcceptFrom,
                survey.AcceptTo,
                survey.ResponseLimit,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task PublishAsync(
        Guid surveyId,
        int version,
        SurveyDefinition definition,
        MappingDefinition mapping,
        Guid? publishedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **版は不変。** 既にある版は上書きしない
        var exists = await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [SurveyVersions] "
            + "WHERE [SurveyId] = @SurveyId AND [Version] = @Version",
            new { SurveyId = surveyId, Version = version },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (exists > 0)
        {
            throw new InvalidOperationException(
                $"版 {version} は既に公開されている。版は不変なので上書きしない");
        }

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SurveyVersions] "
            + "  ([SurveyId], [Version], [DefinitionJson], [MappingJson], "
            + "   [PublishedAt], [PublishedBy]) "
            + "VALUES (@SurveyId, @Version, @DefinitionJson, @MappingJson, @Now, @PublishedBy)",
            new
            {
                SurveyId = surveyId,
                Version = version,
                DefinitionJson = SurveyJson.Serialize(definition),
                MappingJson = SurveyJson.Serialize(mapping),
                Now = DbTime.UtcNowTruncated(),
                PublishedBy = publishedBy,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [Surveys] SET [PublishedVersion] = @Version, [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId, Version = version, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SurveyRecord?> FindByPublicIdAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<SurveyRecord>(Sql(
            "SELECT [SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "       [ResponseJsonColumn], [Status], [PublishedVersion], "
            + "       [AcceptFrom], [AcceptTo], [ResponseLimit], [IsTemplate] "
            + "FROM [Surveys] WHERE [PublicId] = @PublicId",
            new { PublicId = publicId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SurveyRecord?> FindBySurveyIdAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<SurveyRecord>(Sql(
            "SELECT [SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "       [ResponseJsonColumn], [Status], [PublishedVersion], "
            + "       [AcceptFrom], [AcceptTo], [ResponseLimit], [IsTemplate] "
            + "FROM [Surveys] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private DatabaseProvider Provider => connectionFactory.Provider;

    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む。**</summary>
    /// <remarks>
    /// **生の文字列連結をしない**ための口（<c>SqlDialect.Format</c>）。
    /// 角括弧の中だけが RDBMS ごとの引用符へ書き換わる。
    /// </remarks>
    private CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, transaction, cancellationToken: cancellationToken);

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
