using System.Collections.Immutable;
using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

namespace VehicleVision.PleasanterTools.Questionnaire.Data;

/// <summary>編集中のアンケート 1 件。</summary>
/// <param name="Definition">設問の定義。<see cref="SurveyDefinition.Version"/> は**次に公開される版**。</param>
/// <param name="Mapping">Pleasanter の列への割り当て。</param>
/// <param name="Revision">下書きの版。**保存時に照合して、黙った上書きを防ぐ。**</param>
public sealed record SurveyDraft(
    SurveyDefinition Definition,
    MappingDefinition Mapping,
    int Revision);

/// <summary>一覧に出すアンケートの要約。</summary>
/// <param name="SuspendedReason">
/// 止まっている理由（<see cref="SurveySuspendedReason"/>）。**止まっていなければ <c>null</c>。**
/// **管理画面で「なぜ止まっているのか」が分かること**
/// （<c>_documents/データモデル設計.md</c> 2.1）。
/// </param>
/// <param name="SuspendedAt">止めた時刻（UTC）。</param>
/// <param name="ResponseLimit">受け付ける回答の上限。<c>null</c> なら上限なし。</param>
/// <param name="ResponseCount">
/// 受け付けた回答の件数。**まだ Pleasanter へ届いていない分も含む**
/// （<see cref="IResponseTokenStore.CountAcceptedAsync"/>）。
/// </param>
/// <param name="RequireProofOfWork">
/// 回答の送信に proof-of-work を課すか（Issue #66）。**既定は有効。**
/// </param>
public sealed record SurveySummary(
    Guid SurveyId,
    string PublicId,
    string Title,
    long PleasanterSiteId,
    int Status,
    int? PublishedVersion,
    DateTime UpdatedAt,
    int? SuspendedReason = null,
    DateTime? SuspendedAt = null,
    int? ResponseLimit = null,
    int ResponseCount = 0,
    bool RequireProofOfWork = true);

/// <summary>複製で作るアンケートの、**写さない値**（Issue #46）。</summary>
/// <param name="SurveyId">複製先の内部 ID。</param>
/// <param name="PublicId">
/// **新しく作った推測不能な値**（<see cref="Core.Definitions.SurveyPublicId.Generate"/>）。
/// **元の値を使い回さない**（<c>_documents/データモデル設計.md</c> 3 章）。
/// </param>
/// <param name="PleasanterSiteId">
/// 書き込み先のサイト。**元の値を写さない。** 1 アンケート = 1 サイトなので、
/// 写すと 2 つのアンケートが同じサイトへ書き込む。
/// </param>
/// <param name="ResponseJsonColumn">
/// 回答 JSON の正本を入れる列。**サイトに紐づく値なので、元の値を写さない。**
/// </param>
public sealed record SurveyDuplicationTarget(
    Guid SurveyId,
    string PublicId,
    long PleasanterSiteId,
    string? ResponseJsonColumn);

/// <summary>一覧に出すテンプレートの要約（Issue #58）。</summary>
/// <remarks>
/// **サイト ID も公開用 ID も出さない。** テンプレートは書き込み先を持たず、
/// 公開もされないので、画面に出す意味が無い。
/// </remarks>
/// <param name="TemplateId">テンプレートの内部 ID。**<c>Surveys</c> の行 1 つ。**</param>
public sealed record SurveyTemplateSummary(
    Guid TemplateId,
    string Title,
    DateTime UpdatedAt);

/// <summary>テンプレートとして作る行の、**写さない値**（Issue #58）。</summary>
/// <param name="TemplateId">作るテンプレートの内部 ID。</param>
/// <param name="PublicId">
/// **新しく作った推測不能な値**（<see cref="Core.Definitions.SurveyPublicId.Generate"/>）。
/// テンプレートは公開しないので誰にも渡らないが、**列が一意かつ NOT NULL** なので入れる。
/// **元の値を使い回さない**（<c>_documents/データモデル設計.md</c> 3 章）。
/// </param>
public sealed record SurveyTemplateTarget(
    Guid TemplateId,
    string PublicId);

/// <summary>下書きが、読んだ後に他の人に書き換えられていた。</summary>
/// <remarks>
/// **黙って上書きしない。** 管理画面で「他の人が更新した」と伝えて読み直させる。
/// </remarks>
public sealed class SurveyDraftConflictException(int expected, int actual)
    : InvalidOperationException($"下書きの版が違う（送られてきた版 {expected} / 今の版 {actual}）")
{
    public int Expected { get; } = expected;

    public int Actual { get; } = actual;
}

/// <summary>編集中の定義を読み書きする。</summary>
/// <remarks>
/// <para>
/// **公開済みのスナップショットとは別**（<c>_documents/データモデル設計.md</c> 1 章）。
/// ここへの書き込みは回答画面へ影響しない。
/// </para>
/// <para>
/// **まるごと読み、まるごと書く。** 設問の並べ替えや削除は他の設問へ波及するため、
/// 部分更新にすると画面と DB の食い違いを常に疑うことになる。
/// **書き込みは 1 つのトランザクションで入れ替える。**
/// </para>
/// </remarks>
public interface ISurveyDraftStore
{
    Task<IReadOnlyList<SurveySummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<SurveyDraft?> LoadAsync(Guid surveyId, CancellationToken cancellationToken = default);

    /// <summary>下書きを入れ替える。</summary>
    /// <returns>新しい下書きの版。</returns>
    /// <exception cref="SurveyDraftConflictException">
    /// 読んだ後に他の人が書き換えていたとき。
    /// </exception>
    Task<int> SaveAsync(
        Guid surveyId,
        SurveyDefinition definition,
        MappingDefinition mapping,
        int expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>アンケートを丸ごと写して、新しい**下書き**を作る（Issue #46）。</summary>
    /// <param name="sourceSurveyId">写す元のアンケート。</param>
    /// <param name="target">写さない値。**呼ぶ側が決める。**</param>
    /// <returns>元のアンケートが無ければ <c>false</c>。</returns>
    /// <remarks>
    /// **アンケートの行と下書きを 1 つのトランザクションで入れる。**
    /// 途中で失敗したときに、設問の無いアンケートや、
    /// 親の無いページが残ると、画面からも消せなくなる。
    /// </remarks>
    Task<bool> DuplicateAsync(
        Guid sourceSurveyId,
        SurveyDuplicationTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>テンプレートの一覧（Issue #58）。</summary>
    /// <remarks>**アンケートの一覧には出さない。** 逆も同じ。</remarks>
    Task<IReadOnlyList<SurveyTemplateSummary>> ListTemplatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>アンケートを丸ごと写して、**テンプレート**を作る（Issue #58）。</summary>
    /// <param name="sourceSurveyId">写す元のアンケート。**テンプレートは指定できない。**</param>
    /// <returns>元が無い、または元がテンプレートだったときは <c>false</c>。</returns>
    Task<bool> SaveAsTemplateAsync(
        Guid sourceSurveyId,
        SurveyTemplateTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>テンプレートを丸ごと写して、新しい**下書き**を作る（Issue #58）。</summary>
    /// <param name="templateId">写す元のテンプレート。**アンケートは指定できない。**</param>
    /// <param name="target">
    /// 写さない値。**書き込み先のサイトは呼ぶ側が決める**
    /// （テンプレートは持っていない）。
    /// </param>
    /// <returns>元が無い、または元がテンプレートでなかったときは <c>false</c>。</returns>
    Task<bool> CreateFromTemplateAsync(
        Guid templateId,
        SurveyDuplicationTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>テンプレートを消す（Issue #58）。</summary>
    /// <returns>そのテンプレートが無かったときは <c>false</c>。</returns>
    /// <remarks>
    /// **消せるのはテンプレートだけ。** アンケートには回答が紐づいており、
    /// 消すと Pleasanter 側に残った回答の出どころが辿れなくなる。
    /// </remarks>
    Task<bool> DeleteTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper を使った実装。</summary>
public sealed class SurveyDraftStore(IDbConnectionFactory connectionFactory) : ISurveyDraftStore
{
    private sealed record SurveyRow(
        Guid SurveyId,
        string? TitleJson,
        string? DescriptionJson,
        string? ConfirmationMessageJson,
        int DisplayMode,
        bool ShowProgress,
        bool AllowEditingAfterSubmit,
        string? ThemeJson,
        int? PublishedVersion,
        int DraftRevision);

    private sealed record PageRow(
        string PageId,
        string? TitleJson,
        string? DescriptionJson,
        string? NextJson);

    private sealed record QuestionRow(
        string QuestionId,
        string PageId,
        int QuestionType,
        string TitleJson,
        string? DescriptionJson,
        bool IsRequired,
        string? SettingsJson,
        string? VisibleWhenJson);

    private sealed record ChoiceRow(
        string QuestionId,
        string Value,
        string LabelJson,
        bool IsOther,
        string? NextJson);

    private sealed record AssignmentRow(
        Guid AssignmentId,
        string TargetColumn,
        string? ConverterOperation,
        string? ConverterConfigJson);

    private sealed record SourceRow(
        Guid AssignmentId,
        string QuestionId,
        int Port,
        string? RowId);

    /// <summary>複製で写すヘッダ画像の 1 行。</summary>
    private sealed record AssetRow(
        string ContentType, string FileName, long ByteSize, string ContentBase64);

    private DatabaseProvider Provider => connectionFactory.Provider;

    /// <summary>一覧の 1 行を受ける。</summary>
    /// <remarks>
    /// **<c>COUNT</c> の型が 3 者で違う**（SQL Server は <c>int</c>、
    /// PostgreSQL と MySQL は <c>bigint</c>）。**位置引数の record で受けないこと。**
    /// Dapper は引数の型で組み立て先を探すので、どちらで書いても
    /// いずれかの RDBMS で「合う組み立て方が無い」と言って落ちる
    /// （<c>ResponseOutbox.OutboxStatusRow</c> と同じ理由）。
    /// </remarks>
    private sealed class SummaryRow
    {
        public Guid SurveyId { get; set; }

        public string PublicId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public long PleasanterSiteId { get; set; }

        public int Status { get; set; }

        public int? PublishedVersion { get; set; }

        public int? SuspendedReason { get; set; }

        public DateTime? SuspendedAt { get; set; }

        public int? ResponseLimit { get; set; }

        public bool RequireProofOfWork { get; set; }

        public long ResponseCount { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    public async Task<IReadOnlyList<SurveySummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // **テンプレートは出さない**（Issue #58）。
        // 書き込み先も公開用 URL も持たないので、アンケートの表に並べると
        // 「未公開のアンケート」に見えてしまう。
        //
        // **受付数は相関副問い合わせで一緒に読む**（Issue #53）。画面を開くたびに
        // アンケートの本数だけ問い合わせを増やさない。
        // **GROUP BY は使わない**（MySQL の ONLY_FULL_GROUP_BY で書き分けが要る）
        var rows = await connection.QueryAsync<SummaryRow>(Sql(
            "SELECT s.[SurveyId], s.[PublicId], s.[Title], s.[PleasanterSiteId], "
            + "       s.[Status], s.[PublishedVersion], s.[UpdatedAt], "
            + "       s.[SuspendedReason], s.[SuspendedAt], s.[ResponseLimit], "
            + "       s.[RequireProofOfWork], "
            + "       (SELECT COUNT(*) FROM [ResponseTokens] t "
            + "        WHERE t.[SurveyId] = s.[SurveyId]) AS [ResponseCount] "
            + "FROM [Surveys] s WHERE s.[IsTemplate] = @IsTemplate "
            + "ORDER BY s.[UpdatedAt] DESC",
            new { IsTemplate = false },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new SurveySummary(
                row.SurveyId,
                row.PublicId,
                row.Title,
                row.PleasanterSiteId,
                row.Status,
                row.PublishedVersion,
                row.UpdatedAt,
                row.SuspendedReason,
                row.SuspendedAt,
                row.ResponseLimit,
                (int)row.ResponseCount,
                row.RequireProofOfWork)),
        ];
    }

    public async Task<IReadOnlyList<SurveyTemplateSummary>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SurveyTemplateSummary>(Sql(
            "SELECT [SurveyId] AS [TemplateId], [Title], [UpdatedAt] "
            + "FROM [Surveys] WHERE [IsTemplate] = @IsTemplate "
            + "ORDER BY [UpdatedAt] DESC",
            new { IsTemplate = true },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<SurveyDraft?> LoadAsync(
        Guid surveyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, transaction: null, surveyId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>下書きを読む。**トランザクションの中からも呼べる。**</summary>
    /// <remarks>
    /// **複製は読みと書きを 1 つのトランザクションに入れる**ため、
    /// 接続とトランザクションを外から渡せるようにしてある。
    /// 読んだ後に元が書き換わると、写した先が新旧の混ざったものになる。
    /// </remarks>
    private async Task<SurveyDraft?> LoadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid surveyId,
        CancellationToken cancellationToken)
    {
        var survey = await connection.QueryFirstOrDefaultAsync<SurveyRow>(Sql(
            "SELECT [SurveyId], [TitleJson], [DescriptionJson], "
            + "       [ConfirmationMessageJson], [DisplayMode], [ShowProgress], "
            + "       [AllowEditingAfterSubmit], [ThemeJson], [PublishedVersion], "
            + "       [DraftRevision] "
            + "FROM [Surveys] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (survey is null)
        {
            return null;
        }

        var pages = (await connection.QueryAsync<PageRow>(Sql(
            "SELECT [PageId], [TitleJson], [DescriptionJson], [NextJson] FROM [Pages] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var questions = (await connection.QueryAsync<QuestionRow>(Sql(
            "SELECT [QuestionId], [PageId], [QuestionType], [TitleJson], "
            + "       [DescriptionJson], [IsRequired], [SettingsJson], [VisibleWhenJson] "
            + "FROM [Questions] WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        // **設問の一覧で絞る。** アンケートを跨いだ選択肢が混ざらないようにする
        var choices = (await connection.QueryAsync<ChoiceRow>(Sql(
            "SELECT [QuestionId], [Value], [LabelJson], [IsOther], [NextJson] "
            + "FROM [QuestionChoices] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var assignments = (await connection.QueryAsync<AssignmentRow>(Sql(
            "SELECT [AssignmentId], [TargetColumn], [ConverterOperation], "
            + "       [ConverterConfigJson] FROM [ColumnAssignments] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var sources = (await connection.QueryAsync<SourceRow>(Sql(
            "SELECT s.[AssignmentId], s.[QuestionId], s.[Port], s.[RowId] "
            + "FROM [AssignmentSources] s "
            + "JOIN [ColumnAssignments] a ON a.[AssignmentId] = s.[AssignmentId] "
            + "WHERE a.[SurveyId] = @SurveyId ORDER BY s.[SortOrder]",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var choicesByQuestion = choices
            .GroupBy(choice => choice.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(choice => new Choice(
                        choice.Value, ReadText(choice.LabelJson) ?? LocalizedText.Japanese(choice.Value),
                        choice.IsOther,
                        Read<PageTransition>(choice.NextJson)))
                    .ToImmutableArray(),
                StringComparer.Ordinal);

        var questionsByPage = questions
            .GroupBy(question => question.PageId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(question => new Question
                    {
                        QuestionId = question.QuestionId,
                        Type = (QuestionType)question.QuestionType,
                        Title = ReadText(question.TitleJson) ?? LocalizedText.Japanese(string.Empty),
                        Description = ReadText(question.DescriptionJson),
                        IsRequired = question.IsRequired,
                        Choices = choicesByQuestion.GetValueOrDefault(question.QuestionId, []),
                        Settings = question.SettingsJson is null
                            ? new QuestionSettings()
                            : SurveyJson.Deserialize<QuestionSettings>(question.SettingsJson)
                                ?? new QuestionSettings(),
                        VisibleWhen = Read<VisibilityCondition>(question.VisibleWhenJson),
                    })
                    .ToImmutableArray(),
                StringComparer.Ordinal);

        var sourcesByAssignment = sources
            .GroupBy(source => source.AssignmentId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(source => new MappingSource(
                        source.QuestionId, (QuestionPort)source.Port, source.RowId))
                    .ToImmutableArray());

        var definition = new SurveyDefinition
        {
            SurveyId = surveyId.ToString(),
            // **次に公開される版を入れておく。** 公開して初めて確定する
            Version = (survey.PublishedVersion ?? 0) + 1,
            Title = ReadText(survey.TitleJson) ?? LocalizedText.Japanese(string.Empty),
            Description = ReadText(survey.DescriptionJson),
            ConfirmationMessage = ReadText(survey.ConfirmationMessageJson),
            DisplayMode = (DisplayMode)survey.DisplayMode,
            ShowProgress = survey.ShowProgress,
            AllowEditingAfterSubmit = survey.AllowEditingAfterSubmit,
            // **読んだ時点で形を検査する**（Issue #56）。DB を直接書き換えられた行や、
            // 検査を足す前に保存された行を、そのまま画面へ流さない
            Theme = ReadTheme(survey.ThemeJson),
            Pages = pages
                .Select(page => new Page
                {
                    PageId = page.PageId,
                    Title = ReadText(page.TitleJson),
                    Description = ReadText(page.DescriptionJson),
                    Questions = questionsByPage.GetValueOrDefault(page.PageId, []),
                    Next = Read<PageTransition>(page.NextJson),
                })
                .ToImmutableArray(),
        };

        var mapping = new MappingDefinition
        {
            Assignments = assignments
                .Select(assignment => new ColumnAssignment
                {
                    TargetColumn = assignment.TargetColumn,
                    Sources = sourcesByAssignment.GetValueOrDefault(assignment.AssignmentId, []),
                    Converter = assignment.ConverterOperation is null
                        ? null
                        : new MappingConverter(
                            assignment.ConverterOperation,
                            ReadConfig(assignment.ConverterConfigJson)),
                })
                .ToImmutableArray(),
        };

        return new SurveyDraft(definition, mapping, survey.DraftRevision);
    }

    public async Task<int> SaveAsync(
        Guid surveyId,
        SurveyDefinition definition,
        MappingDefinition mapping,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **版が合う行だけ進める。** 読んでから比べると、間に入った更新を取りこぼす
        var advanced = await connection.ExecuteAsync(Sql(
            "UPDATE [Surveys] SET "
            + "  [DraftRevision] = [DraftRevision] + 1, "
            + "  [TitleJson] = @TitleJson, [DescriptionJson] = @DescriptionJson, "
            + "  [ConfirmationMessageJson] = @ConfirmationMessageJson, "
            + "  [DisplayMode] = @DisplayMode, [ShowProgress] = @ShowProgress, "
            + "  [AllowEditingAfterSubmit] = @AllowEditingAfterSubmit, "
            + "  [ThemeJson] = @ThemeJson, "
            + "  [Title] = @Title, [UpdatedAt] = @Now "
            + "WHERE [SurveyId] = @SurveyId AND [DraftRevision] = @ExpectedRevision",
            new
            {
                SurveyId = surveyId,
                ExpectedRevision = expectedRevision,
                TitleJson = WriteText(definition.Title),
                DescriptionJson = WriteText(definition.Description),
                ConfirmationMessageJson = WriteText(definition.ConfirmationMessage),
                DisplayMode = (int)definition.DisplayMode,
                definition.ShowProgress,
                definition.AllowEditingAfterSubmit,
                ThemeJson = WriteTheme(definition.Theme),
                // 一覧に出す用の平文。**多言語の正本は TitleJson**
                Title = Shorten(definition.Title.Get(LocalizedText.DefaultLanguage), 512),
                Now = DbTime.UtcNowTruncated(),
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (advanced != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            var actual = await connection.QueryFirstOrDefaultAsync<int?>(Sql(
                "SELECT [DraftRevision] FROM [Surveys] WHERE [SurveyId] = @SurveyId",
                new { SurveyId = surveyId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            throw actual is null
                ? new InvalidOperationException($"アンケート {surveyId} が見つからない")
                : new SurveyDraftConflictException(expectedRevision, actual.Value);
        }

        await DeleteDraftAsync(connection, transaction, surveyId, cancellationToken).ConfigureAwait(false);
        await InsertDraftAsync(connection, transaction, surveyId, definition, mapping, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expectedRevision + 1;
    }

    public Task<bool> DuplicateAsync(
        Guid sourceSurveyId,
        SurveyDuplicationTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        return CopyAsync(
            sourceSurveyId,
            sourceIsTemplate: false,
            new CopyTarget(
                target.SurveyId,
                target.PublicId,
                target.PleasanterSiteId,
                target.ResponseJsonColumn,
                IsTemplate: false,
                // **元と並ぶので「のコピー」を付ける。** 同じ題名が 2 つ並ぶと見分けられない
                RenameAsCopy: true),
            cancellationToken);
    }

    public Task<bool> SaveAsTemplateAsync(
        Guid sourceSurveyId,
        SurveyTemplateTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        return CopyAsync(
            sourceSurveyId,
            sourceIsTemplate: false,
            new CopyTarget(
                target.TemplateId,
                target.PublicId,
                // **テンプレートは書き込み先を持たない**（Issue #58）。
                // そこから作るときに指定させる
                NoPleasanterSiteId,
                ResponseJsonColumn: null,
                IsTemplate: true,
                // **テンプレートには「のコピー」を付けない。** 元と並べて置くものではない
                RenameAsCopy: false),
            cancellationToken);
    }

    public Task<bool> CreateFromTemplateAsync(
        Guid templateId,
        SurveyDuplicationTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        return CopyAsync(
            templateId,
            sourceIsTemplate: true,
            new CopyTarget(
                target.SurveyId,
                target.PublicId,
                target.PleasanterSiteId,
                target.ResponseJsonColumn,
                IsTemplate: false,
                // **テンプレートから作るものに「のコピー」は付けない。**
                // 元は並んで見えないので、付けても何の写しか分からない
                RenameAsCopy: false),
            cancellationToken);
    }

    public async Task<bool> DeleteTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **先に親を消して、テンプレートであることを SQL の側で確かめる。**
        // 子から消すと、間違ってアンケートの ID を渡されたときに
        // 設問だけ消えて行が残る
        var deleted = await connection.ExecuteAsync(Sql(
            "DELETE FROM [Surveys] "
            + "WHERE [SurveyId] = @SurveyId AND [IsTemplate] = @IsTemplate",
            new { SurveyId = templateId, IsTemplate = true },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (deleted != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await DeleteDraftAsync(connection, transaction, templateId, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>写して 1 行作るときの、**写さない値**。</summary>
    /// <remarks>
    /// **複製もテンプレートも、写すものは同じで写さないものだけが違う。**
    /// 道を分けると、後から足した項目を片方だけ写し漏らす。
    /// </remarks>
    /// <param name="RenameAsCopy">題名の後ろへ「のコピー」を付けるか。</param>
    private sealed record CopyTarget(
        Guid SurveyId,
        string PublicId,
        long PleasanterSiteId,
        string? ResponseJsonColumn,
        bool IsTemplate,
        bool RenameAsCopy);

    /// <summary>テンプレートが持つサイト ID。**「書き込み先が無い」を表す。**</summary>
    /// <remarks>
    /// **列を NULL 可にしない。** 回答を書き込む経路すべてで
    /// 「サイトが無い」を持ち回ることになる。テンプレートは公開できないので
    /// （<c>AdminSurveyEndpoints</c> で断る）、この値が使われることはない。
    /// </remarks>
    private const long NoPleasanterSiteId = 0;

    /// <summary>元を丸ごと写して、新しい行と下書きを作る。</summary>
    /// <param name="sourceIsTemplate">
    /// 元がテンプレートであることを期待するか。**食い違ったら写さない。**
    /// アンケートの ID をテンプレートの口へ渡す（またはその逆）と、
    /// サイトを持たないアンケートや、公開できるテンプレートができてしまう。
    /// </param>
    private async Task<bool> CopyAsync(
        Guid sourceSurveyId,
        bool sourceIsTemplate,
        CopyTarget target,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **同じトランザクションの中で読む。** 読んでから書くまでの間に
        // 元が保存されると、写した先が新旧の混ざったものになる。
        // **種類が食い違ったら何もしない**（アンケートとテンプレートの取り違え）
        var actualIsTemplate = await connection.QueryFirstOrDefaultAsync<bool?>(Sql(
            "SELECT [IsTemplate] FROM [Surveys] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = sourceSurveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (actualIsTemplate != sourceIsTemplate)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var source = await LoadAsync(connection, transaction, sourceSurveyId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var definition = SurveyDuplication.Copy(
            source.Definition, target.SurveyId.ToString(), target.RenameAsCopy);
        var now = DbTime.UtcNowTruncated();

        // **ヘッダ画像は写す**（Issue #56）。画像は元のアンケートに紐づいており、
        // 読み出しをアンケートで絞っている以上、識別子だけ写しても複製先からは読めない。
        // **同じトランザクションの中で写す。** 途中で失敗すると、
        // 出ない画像を指したままの複製が残る
        definition = definition with
        {
            Theme = await CopyHeaderImageAsync(
                connection, transaction, definition.Theme, target.SurveyId, now, cancellationToken)
                .ConfigureAwait(false),
        };

        // **下書きとして作る**（Issue #46）。公開状態も公開済みの版も写さない。
        // **受付期間・回答上限も写さない。** 公開の設定であり、
        // 期限切れの期間を引き継いだ複製は、作った直後から回答できない
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [Surveys] "
            + "  ([SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "   [ResponseJsonColumn], [Status], [PublishedVersion], "
            + "   [DraftRevision], [DisplayMode], [ShowProgress], "
            + "   [AllowEditingAfterSubmit], [TitleJson], [DescriptionJson], "
            + "   [ConfirmationMessageJson], [IsTemplate], [ThemeJson], "
            + "   [CreatedAt], [UpdatedAt]) "
            + "VALUES (@SurveyId, @PublicId, @Title, @PleasanterSiteId, "
            + "        @ResponseJsonColumn, @Status, NULL, "
            + "        0, @DisplayMode, @ShowProgress, "
            + "        @AllowEditingAfterSubmit, @TitleJson, @DescriptionJson, "
            + "        @ConfirmationMessageJson, @IsTemplate, @ThemeJson, @Now, @Now)",
            new
            {
                target.SurveyId,
                target.PublicId,
                target.PleasanterSiteId,
                target.ResponseJsonColumn,
                target.IsTemplate,
                Status = (int)SurveyStatus.Draft,
                DisplayMode = (int)definition.DisplayMode,
                definition.ShowProgress,
                definition.AllowEditingAfterSubmit,
                TitleJson = WriteText(definition.Title),
                DescriptionJson = WriteText(definition.Description),
                ConfirmationMessageJson = WriteText(definition.ConfirmationMessage),
                ThemeJson = WriteTheme(definition.Theme),
                // 一覧に出す用の平文。**多言語の正本は TitleJson**
                Title = Shorten(definition.Title.Get(LocalizedText.DefaultLanguage), 512),
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // **保存と同じ道を通す。** 別に書くと、後から足した項目を
        // 複製だけ写し漏らす（分岐がまさにそうなりやすい）
        await InsertDraftAsync(
            connection, transaction, target.SurveyId, definition, source.Mapping, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task DeleteDraftAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid surveyId,
        CancellationToken cancellationToken)
    {
        // **子から先に消す。** 親を先に消すと、参照先を失った行の掃除が難しくなる
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [AssignmentSources] WHERE [AssignmentId] IN ("
            + "SELECT [AssignmentId] FROM [ColumnAssignments] "
            + "WHERE [SurveyId] = @SurveyId)",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "DELETE FROM [ColumnAssignments] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "DELETE FROM [QuestionChoices] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "DELETE FROM [Questions] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "DELETE FROM [Pages] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task InsertDraftAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid surveyId,
        SurveyDefinition definition,
        MappingDefinition mapping,
        CancellationToken cancellationToken)
    {
        for (var pageIndex = 0; pageIndex < definition.Pages.Length; pageIndex++)
        {
            var page = definition.Pages[pageIndex];

            await connection.ExecuteAsync(Sql(
                "INSERT INTO [Pages] ([PageId], [SurveyId], [SortOrder], "
                + "[TitleJson], [DescriptionJson], [NextJson]) "
                + "VALUES (@PageId, @SurveyId, @SortOrder, @TitleJson, @DescriptionJson, "
                + "@NextJson)",
                new
                {
                    page.PageId,
                    SurveyId = surveyId,
                    SortOrder = pageIndex,
                    TitleJson = WriteText(page.Title),
                    DescriptionJson = WriteText(page.Description),
                    NextJson = Write(page.Next),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            for (var questionIndex = 0; questionIndex < page.Questions.Length; questionIndex++)
            {
                var question = page.Questions[questionIndex];

                await connection.ExecuteAsync(Sql(
                    "INSERT INTO [Questions] ([QuestionId], [SurveyId], [PageId], "
                    + "[SortOrder], [QuestionType], [TitleJson], "
                    + "[DescriptionJson], [IsRequired], [SettingsJson], [VisibleWhenJson]) "
                    + "VALUES (@QuestionId, @SurveyId, @PageId, @SortOrder, @QuestionType, "
                    + "@TitleJson, @DescriptionJson, @IsRequired, @SettingsJson, @VisibleWhenJson)",
                    new
                    {
                        question.QuestionId,
                        SurveyId = surveyId,
                        page.PageId,
                        SortOrder = questionIndex,
                        QuestionType = (int)question.Type,
                        TitleJson = WriteText(question.Title),
                        DescriptionJson = WriteText(question.Description),
                        question.IsRequired,
                        SettingsJson = SurveyJson.Serialize(question.Settings),
                        VisibleWhenJson = Write(question.VisibleWhen),
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                for (var choiceIndex = 0; choiceIndex < question.Choices.Length; choiceIndex++)
                {
                    var choice = question.Choices[choiceIndex];

                    await connection.ExecuteAsync(Sql(
                        "INSERT INTO [QuestionChoices] ([ChoiceId], [SurveyId], "
                        + "[QuestionId], [SortOrder], [Value], [LabelJson], "
                        + "[IsOther], [NextJson]) "
                        + "VALUES (@ChoiceId, @SurveyId, @QuestionId, @SortOrder, @Value, "
                        + "@LabelJson, @IsOther, @NextJson)",
                        new
                        {
                            ChoiceId = Guid.NewGuid(),
                            SurveyId = surveyId,
                            question.QuestionId,
                            SortOrder = choiceIndex,
                            choice.Value,
                            LabelJson = WriteText(choice.Label),
                            choice.IsOther,
                            NextJson = Write(choice.Next),
                        },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }
        }

        for (var index = 0; index < mapping.Assignments.Length; index++)
        {
            var assignment = mapping.Assignments[index];
            var assignmentId = Guid.NewGuid();

            await connection.ExecuteAsync(Sql(
                "INSERT INTO [ColumnAssignments] ([AssignmentId], [SurveyId], "
                + "[TargetColumn], [ConverterOperation], [ConverterConfigJson], "
                + "[SortOrder]) "
                + "VALUES (@AssignmentId, @SurveyId, @TargetColumn, @ConverterOperation, "
                + "@ConverterConfigJson, @SortOrder)",
                new
                {
                    AssignmentId = assignmentId,
                    SurveyId = surveyId,
                    assignment.TargetColumn,
                    ConverterOperation = assignment.Converter?.Operation,
                    ConverterConfigJson = assignment.Converter is null
                        ? null
                        : SurveyJson.Serialize(assignment.Converter.Config),
                    SortOrder = index,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            for (var sourceIndex = 0; sourceIndex < assignment.Sources.Length; sourceIndex++)
            {
                var source = assignment.Sources[sourceIndex];

                await connection.ExecuteAsync(Sql(
                    "INSERT INTO [AssignmentSources] ([SourceId], [AssignmentId], "
                    + "[QuestionId], [Port], [RowId], [SortOrder]) "
                    + "VALUES (@SourceId, @AssignmentId, @QuestionId, @Port, @RowId, "
                    + "@SortOrder)",
                    new
                    {
                        SourceId = Guid.NewGuid(),
                        AssignmentId = assignmentId,
                        source.QuestionId,
                        Port = (int)source.Port,
                        source.RowId,
                        SortOrder = sourceIndex,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>ヘッダ画像を複製先へ写し、新しい識別子を指すテーマを返す。</summary>
    /// <remarks>
    /// <para>
    /// **中身を読んで入れ直す。** <c>INSERT ... SELECT</c> で同じ表を跨ぐ書き方は
    /// 3 者で通り方が揃わない（<c>_documents/データモデル設計.md</c> 4 章）。
    /// 画像は 1 枚・数 MB なので、読んで書く方が安い。
    /// </para>
    /// <para>
    /// **元の画像が見つからなければ、指定ごと落とす。** 出ない画像を指したままにすると、
    /// 複製した人は「設定してあるのに出ない」を追うことになる。
    /// </para>
    /// </remarks>
    private async Task<SurveyTheme?> CopyHeaderImageAsync(
        DbConnection connection,
        DbTransaction transaction,
        SurveyTheme? theme,
        Guid targetSurveyId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (theme?.HeaderImage() is not { } assetId)
        {
            return theme;
        }

        var source = await connection.QueryFirstOrDefaultAsync<AssetRow>(Sql(
            "SELECT [ContentType], [FileName], [ByteSize], [ContentBase64] "
            + "FROM [SurveyAssets] WHERE [AssetId] = @AssetId",
            new { AssetId = assetId },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (source is null)
        {
            return theme with { HeaderImageId = null };
        }

        var copiedId = Guid.NewGuid();

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SurveyAssets] "
            + "  ([AssetId], [SurveyId], [ContentType], [FileName], [ByteSize], "
            + "   [ContentBase64], [CreatedAt]) "
            + "VALUES (@AssetId, @SurveyId, @ContentType, @FileName, @ByteSize, "
            + "        @ContentBase64, @Now)",
            new
            {
                AssetId = copiedId,
                SurveyId = targetSurveyId,
                source.ContentType,
                source.FileName,
                source.ByteSize,
                source.ContentBase64,
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return theme with { HeaderImageId = copiedId.ToString() };
    }

    /// <summary>テーマを読む。**形の正しくない値は捨てる。**</summary>
    /// <remarks>
    /// **既定と区別する。** 何も指定していないテーマは <c>null</c> にして返す。
    /// 空のテーマを返すと、触っていない定義の JSON にも <c>theme</c> が載る。
    /// </remarks>
    private static SurveyTheme? ReadTheme(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var theme = SurveyJson.Deserialize<SurveyTheme>(json)?.Sanitized();
        return theme is null || theme.IsDefault ? null : theme;
    }

    /// <summary>テーマを書く。**既定なら NULL。**</summary>
    private static string? WriteTheme(SurveyTheme? theme)
    {
        var sanitized = theme?.Sanitized();
        return sanitized is null || sanitized.IsDefault ? null : SurveyJson.Serialize(sanitized);
    }

    private static LocalizedText? ReadText(string? json) =>
        json is null ? null : SurveyJson.Deserialize<LocalizedText>(json);

    private static string? WriteText(LocalizedText? text) =>
        text is null ? null : SurveyJson.Serialize(text);

    private static ImmutableDictionary<string, string> ReadConfig(string? json) =>
        json is null
            ? ImmutableDictionary<string, string>.Empty
            : SurveyJson.Deserialize<Dictionary<string, string>>(json)
                ?.ToImmutableDictionary(StringComparer.Ordinal)
                ?? ImmutableDictionary<string, string>.Empty;

    /// <summary>一覧用の平文を列の桁に収める。</summary>
    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max];


    /// <summary>SQL を組み立てる。**識別子は角括弧で囲む。**</summary>
    /// <remarks>
    /// **生の文字列連結をしない**ための口（<c>SqlDialect.Format</c>）。
    /// 角括弧の中だけが RDBMS ごとの引用符へ書き換わる。
    /// </remarks>
    /// <summary>分岐の設定を読む。**壊れていたら「無い」として扱う。**</summary>
    /// <remarks>
    /// **例外にしない。** 1 か所の壊れで下書きがまるごと開けなくなると、
    /// 直す手立てが DB の直接操作しか無くなる。
    /// </remarks>
    private static T? Read<T>(string? json)
        where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : SurveyJson.Deserialize<T>(json);

    /// <summary>分岐の設定を書く。**無いときは NULL。**</summary>
    private static string? Write<T>(T? value)
        where T : class =>
        value is null ? null : SurveyJson.Serialize(value);

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
