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

    public async Task<SurveySnapshot?> FindAsync(
        Guid surveyId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);

        await using var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT v.{q("DefinitionJson")}, v.{q("MappingJson")}, "
            + $"       s.{q("PleasanterSiteId")}, s.{q("ResponseJsonColumn")} "
            + $"FROM {q("SurveyVersions")} v "
            + $"JOIN {q("Surveys")} s ON s.{q("SurveyId")} = v.{q("SurveyId")} "
            + $"WHERE v.{q("SurveyId")} = @SurveyId AND v.{q("Version")} = @Version",
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
    int? ResponseLimit = null);

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
    public async Task SaveAsync(SurveyRecord survey, CancellationToken cancellationToken = default)
    {
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);
        var now = DbTime.UtcNowTruncated();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {q("Surveys")} SET "
            + $"  {q("PublicId")} = @PublicId, {q("Title")} = @Title, "
            + $"  {q("PleasanterSiteId")} = @PleasanterSiteId, "
            + $"  {q("ResponseJsonColumn")} = @ResponseJsonColumn, "
            + $"  {q("Status")} = @Status, {q("PublishedVersion")} = @PublishedVersion, "
            + $"  {q("AcceptFrom")} = @AcceptFrom, {q("AcceptTo")} = @AcceptTo, "
            + $"  {q("ResponseLimit")} = @ResponseLimit, "
            + $"  {q("UpdatedAt")} = @Now "
            + $"WHERE {q("SurveyId")} = @SurveyId",
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

        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO {q("Surveys")} "
            + $"  ({q("SurveyId")}, {q("PublicId")}, {q("Title")}, {q("PleasanterSiteId")}, "
            + $"   {q("ResponseJsonColumn")}, {q("Status")}, {q("PublishedVersion")}, "
            + $"   {q("AcceptFrom")}, {q("AcceptTo")}, {q("ResponseLimit")}, "
            + $"   {q("CreatedAt")}, {q("UpdatedAt")}) "
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
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **版は不変。** 既にある版は上書きしない
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {q("SurveyVersions")} "
            + $"WHERE {q("SurveyId")} = @SurveyId AND {q("Version")} = @Version",
            new { SurveyId = surveyId, Version = version },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (exists > 0)
        {
            throw new InvalidOperationException(
                $"版 {version} は既に公開されている。版は不変なので上書きしない");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO {q("SurveyVersions")} "
            + $"  ({q("SurveyId")}, {q("Version")}, {q("DefinitionJson")}, {q("MappingJson")}, "
            + $"   {q("PublishedAt")}, {q("PublishedBy")}) "
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

        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE {q("Surveys")} SET {q("PublishedVersion")} = @Version, {q("UpdatedAt")} = @Now "
            + $"WHERE {q("SurveyId")} = @SurveyId",
            new { SurveyId = surveyId, Version = version, Now = DbTime.UtcNowTruncated() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SurveyRecord?> FindByPublicIdAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<SurveyRecord>(new CommandDefinition(
            $"SELECT {q("SurveyId")}, {q("PublicId")}, {q("Title")}, {q("PleasanterSiteId")}, "
            + $"       {q("ResponseJsonColumn")}, {q("Status")}, {q("PublishedVersion")}, "
            + $"       {q("AcceptFrom")}, {q("AcceptTo")}, {q("ResponseLimit")} "
            + $"FROM {q("Surveys")} WHERE {q("PublicId")} = @PublicId",
            new { PublicId = publicId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SurveyRecord?> FindBySurveyIdAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        var q = (string name) => SqlDialect.Quote(connectionFactory.Provider, name);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<SurveyRecord>(new CommandDefinition(
            $"SELECT {q("SurveyId")}, {q("PublicId")}, {q("Title")}, {q("PleasanterSiteId")}, "
            + $"       {q("ResponseJsonColumn")}, {q("Status")}, {q("PublishedVersion")}, "
            + $"       {q("AcceptFrom")}, {q("AcceptTo")}, {q("ResponseLimit")} "
            + $"FROM {q("Surveys")} WHERE {q("SurveyId")} = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
