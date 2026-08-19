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
public sealed record SurveySummary(
    Guid SurveyId,
    string PublicId,
    string Title,
    long PleasanterSiteId,
    int Status,
    int? PublishedVersion,
    DateTime UpdatedAt);

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

    private sealed record SourceRow(Guid AssignmentId, string QuestionId, int Port);

    private DatabaseProvider Provider => connectionFactory.Provider;

    public async Task<IReadOnlyList<SurveySummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SurveySummary>(Sql(
            "SELECT [SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "       [Status], [PublishedVersion], [UpdatedAt] "
            + "FROM [Surveys] ORDER BY [UpdatedAt] DESC",
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
            + "       [AllowEditingAfterSubmit], [PublishedVersion], [DraftRevision] "
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
            "SELECT s.[AssignmentId], s.[QuestionId], s.[Port] "
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
                    .Select(source => new MappingSource(source.QuestionId, (QuestionPort)source.Port))
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

    public async Task<bool> DuplicateAsync(
        Guid sourceSurveyId,
        SurveyDuplicationTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // **同じトランザクションの中で読む。** 読んでから書くまでの間に
        // 元が保存されると、写した先が新旧の混ざったものになる
        var source = await LoadAsync(connection, transaction, sourceSurveyId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var definition = SurveyDuplication.Copy(source.Definition, target.SurveyId.ToString());
        var now = DbTime.UtcNowTruncated();

        // **下書きとして作る**（Issue #46）。公開状態も公開済みの版も写さない。
        // **受付期間・回答上限も写さない。** 公開の設定であり、
        // 期限切れの期間を引き継いだ複製は、作った直後から回答できない
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [Surveys] "
            + "  ([SurveyId], [PublicId], [Title], [PleasanterSiteId], "
            + "   [ResponseJsonColumn], [Status], [PublishedVersion], "
            + "   [DraftRevision], [DisplayMode], [ShowProgress], "
            + "   [AllowEditingAfterSubmit], [TitleJson], [DescriptionJson], "
            + "   [ConfirmationMessageJson], [CreatedAt], [UpdatedAt]) "
            + "VALUES (@SurveyId, @PublicId, @Title, @PleasanterSiteId, "
            + "        @ResponseJsonColumn, @Status, NULL, "
            + "        0, @DisplayMode, @ShowProgress, "
            + "        @AllowEditingAfterSubmit, @TitleJson, @DescriptionJson, "
            + "        @ConfirmationMessageJson, @Now, @Now)",
            new
            {
                target.SurveyId,
                target.PublicId,
                target.PleasanterSiteId,
                target.ResponseJsonColumn,
                Status = (int)SurveyStatus.Draft,
                DisplayMode = (int)definition.DisplayMode,
                definition.ShowProgress,
                definition.AllowEditingAfterSubmit,
                TitleJson = WriteText(definition.Title),
                DescriptionJson = WriteText(definition.Description),
                ConfirmationMessageJson = WriteText(definition.ConfirmationMessage),
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
                    + "[QuestionId], [Port], [SortOrder]) "
                    + "VALUES (@SourceId, @AssignmentId, @QuestionId, @Port, @SortOrder)",
                    new
                    {
                        SourceId = Guid.NewGuid(),
                        AssignmentId = assignmentId,
                        source.QuestionId,
                        Port = (int)source.Port,
                        SortOrder = sourceIndex,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
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
