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
        if (definition is null || mapping is null)
        {
            return null;
        }

        // **見た目の値は、回答画面へ渡す前にここで形を検査する**（Issue #56）。
        // 版は不変なので直せない。**検査を足す前に固まった版**や、
        // DB を直接書き換えられた版が、そのまま回答者のブラウザへ届くのを止める。
        // **公開済みの版そのものは書き換えない。** 読むたびに落とすだけ
        var theme = definition.Theme?.Sanitized();
        definition = definition with { Theme = theme is null || theme.IsDefault ? null : theme };

        return new SurveySnapshot(
            definition, mapping, row.PleasanterSiteId, row.ResponseJsonColumn);
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

    /// <summary>回答数が上限に達したので受付を止める（Issue #53）。</summary>
    /// <returns>このとき止めたなら <c>true</c>。既に止まっていたなら <c>false</c>。</returns>
    /// <remarks>
    /// <para>
    /// **1 文の <c>UPDATE</c> で行う。** 受け付けるたびに走る処理なので、
    /// 読んでから書くと同時の受付どうしで踏み合う。
    /// </para>
    /// <para>
    /// **公開中の行だけを止める**（<c>Status</c> を条件に入れてある）。
    /// これで 2 つのことが同時に守られる。
    /// **手で止めた理由を上書きしない**（既に停止中なら 0 行）ことと、
    /// **下書きへ戻したアンケートを勝手に停止中にしない**こと。
    /// </para>
    /// <para>
    /// **止めるだけで、再開はしない**（<c>_documents/データモデル設計.md</c> 2.1）。
    /// </para>
    /// </remarks>
    Task<bool> SuspendForResponseLimitAsync(
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
/// <param name="ResponseLimit">
/// 受け付ける回答の上限。<c>null</c> なら上限なし。
/// **数えるのは <c>ResponseTokens</c> の行**（<see cref="IResponseTokenStore.CountAcceptedAsync"/>）。
/// </param>
/// <param name="SuspendedReason">
/// 止まっている理由（<see cref="SurveySuspendedReason"/>）。**止まっていなければ <c>null</c>。**
/// </param>
/// <param name="SuspendedAt">止めた時刻（UTC）。</param>
/// <param name="RequireProofOfWork">
/// 回答の送信に proof-of-work を課すか（Issue #66）。**既定は有効。**
///
/// **定義ではなく運用の設定。** 切り替えても公開し直す必要は無い
/// （<c>ResponseLimit</c> や <c>AcceptFrom</c> と同じ扱い）。
///
/// **切っても送信チケット・最短時間・honeypot は外れない。**
/// 外れるのは proof-of-work だけ。
/// </param>
/// <param name="AllowDraft">
/// 回答の下書きを端末へ残すか（Issue #59）。
///
/// ⚠️ **既定は無効。** 端末は共有され得る（店頭のタブレット、社内の共用 PC）。
/// **黙って端末へ残すと、次に使う人が前の人の回答を見る。**
///
/// **サーバへは送らない。** 下書きは端末の中だけに置く。
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
    bool IsTemplate = false,
    int? SuspendedReason = null,
    DateTime? SuspendedAt = null,
    bool RequireProofOfWork = true,
    bool AllowDraft = false);

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

/// <summary>受付を止めている理由。</summary>
/// <remarks>
/// <para>
/// **「管理者が手で止めた」と「閾値で自動停止した」を区別する**
/// （<c>_documents/データモデル設計.md</c> 2.1）。区別が付かないと、
/// 管理画面で「なぜ止まっているのか」が分からない。
/// </para>
/// <para>
/// **0 を使わない。** 列は <c>NULL</c> 可で、<c>NULL</c> が「止まっていない」を表す。
/// 0 を割り当てると、既定値と「手で止めた」が見分けられなくなる。
/// </para>
/// </remarks>
public enum SurveySuspendedReason
{
    /// <summary>管理者が手で止めた。**人が再開する。**</summary>
    Manual = 1,

    /// <summary>回答数が上限に達したので自動で止めた。</summary>
    /// <remarks>
    /// **勝手に再開しない**（<c>_documents/データモデル設計.md</c> 2.1）。
    /// 上限を引き上げるか、そのまま終わらせるかは人が決める。
    /// </remarks>
    ResponseLimitReached = 2,
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
            + "  [SuspendedReason] = @SuspendedReason, [SuspendedAt] = @SuspendedAt, "
            // **旗も書く**（Issue #66）。管理画面の公開設定はここを通る
            + "  [RequireProofOfWork] = @RequireProofOfWork, "
            + "  [AllowDraft] = @AllowDraft, "
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
                survey.SuspendedReason,
                survey.SuspendedAt,
                survey.RequireProofOfWork,
                survey.AllowDraft,
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
            + "   [SuspendedReason], [SuspendedAt], [RequireProofOfWork], [AllowDraft], "
            + "   [CreatedAt], [UpdatedAt]) "
            + "VALUES (@SurveyId, @PublicId, @Title, @PleasanterSiteId, "
            + "        @ResponseJsonColumn, @Status, @PublishedVersion, "
            + "        @AcceptFrom, @AcceptTo, @ResponseLimit, "
            + "        @SuspendedReason, @SuspendedAt, @RequireProofOfWork, @AllowDraft, "
            + "        @Now, @Now)",
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
                survey.SuspendedReason,
                survey.SuspendedAt,
                survey.RequireProofOfWork,
                survey.AllowDraft,
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
            + "       [AcceptFrom], [AcceptTo], [ResponseLimit], [IsTemplate], "
            + "       [SuspendedReason], [SuspendedAt], [RequireProofOfWork], [AllowDraft] "
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
            + "       [AcceptFrom], [AcceptTo], [ResponseLimit], [IsTemplate], "
            + "       [SuspendedReason], [SuspendedAt], [RequireProofOfWork], [AllowDraft] "
            + "FROM [Surveys] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> SuspendForResponseLimitAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // **公開中の行しか止めない。** 既に停止中なら 0 行で終わり、
        // 手で止めた理由（Manual）を上書きしない。何度呼んでも同じ結果になる
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [Surveys] SET "
            + "  [Status] = @SuspendedStatus, "
            + "  [SuspendedReason] = @Reason, "
            + "  [SuspendedAt] = @Now, "
            + "  [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId AND [Status] = @PublishedStatus",
            new
            {
                SurveyId = surveyId,
                SuspendedStatus = (int)SurveyStatus.Suspended,
                PublishedStatus = (int)SurveyStatus.Published,
                Reason = (int)SurveySuspendedReason.ResponseLimitReached,
                Now = DbTime.UtcNowTruncated(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected > 0;
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
