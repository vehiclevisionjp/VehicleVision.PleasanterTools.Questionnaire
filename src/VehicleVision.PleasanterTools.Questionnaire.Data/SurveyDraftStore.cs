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

    private sealed record PageRow(string PageId, string? TitleJson, string? DescriptionJson);

    private sealed record QuestionRow(
        string QuestionId,
        string PageId,
        int QuestionType,
        string TitleJson,
        string? DescriptionJson,
        bool IsRequired,
        string? SettingsJson);

    private sealed record ChoiceRow(string QuestionId, string Value, string LabelJson, bool IsOther);

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

        var survey = await connection.QueryFirstOrDefaultAsync<SurveyRow>(Sql(
            "SELECT [SurveyId], [TitleJson], [DescriptionJson], "
            + "       [ConfirmationMessageJson], [DisplayMode], [ShowProgress], "
            + "       [AllowEditingAfterSubmit], [PublishedVersion], [DraftRevision] "
            + "FROM [Surveys] WHERE [SurveyId] = @SurveyId",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (survey is null)
        {
            return null;
        }

        var pages = (await connection.QueryAsync<PageRow>(Sql(
            "SELECT [PageId], [TitleJson], [DescriptionJson] FROM [Pages] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var questions = (await connection.QueryAsync<QuestionRow>(Sql(
            "SELECT [QuestionId], [PageId], [QuestionType], [TitleJson], "
            + "       [DescriptionJson], [IsRequired], [SettingsJson] "
            + "FROM [Questions] WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        // **設問の一覧で絞る。** アンケートを跨いだ選択肢が混ざらないようにする
        var choices = (await connection.QueryAsync<ChoiceRow>(Sql(
            "SELECT [QuestionId], [Value], [LabelJson], [IsOther] "
            + "FROM [QuestionChoices] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var assignments = (await connection.QueryAsync<AssignmentRow>(Sql(
            "SELECT [AssignmentId], [TargetColumn], [ConverterOperation], "
            + "       [ConverterConfigJson] FROM [ColumnAssignments] "
            + "WHERE [SurveyId] = @SurveyId ORDER BY [SortOrder]",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var sources = (await connection.QueryAsync<SourceRow>(Sql(
            "SELECT s.[AssignmentId], s.[QuestionId], s.[Port] "
            + "FROM [AssignmentSources] s "
            + "JOIN [ColumnAssignments] a ON a.[AssignmentId] = s.[AssignmentId] "
            + "WHERE a.[SurveyId] = @SurveyId ORDER BY s.[SortOrder]",
            new { SurveyId = surveyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var choicesByQuestion = choices
            .GroupBy(choice => choice.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(choice => new Choice(
                        choice.Value, ReadText(choice.LabelJson) ?? LocalizedText.Japanese(choice.Value),
                        choice.IsOther))
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
                + "[TitleJson], [DescriptionJson]) "
                + "VALUES (@PageId, @SurveyId, @SortOrder, @TitleJson, @DescriptionJson)",
                new
                {
                    page.PageId,
                    SurveyId = surveyId,
                    SortOrder = pageIndex,
                    TitleJson = WriteText(page.Title),
                    DescriptionJson = WriteText(page.Description),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            for (var questionIndex = 0; questionIndex < page.Questions.Length; questionIndex++)
            {
                var question = page.Questions[questionIndex];

                await connection.ExecuteAsync(Sql(
                    "INSERT INTO [Questions] ([QuestionId], [SurveyId], [PageId], "
                    + "[SortOrder], [QuestionType], [TitleJson], "
                    + "[DescriptionJson], [IsRequired], [SettingsJson]) "
                    + "VALUES (@QuestionId, @SurveyId, @PageId, @SortOrder, @QuestionType, "
                    + "@TitleJson, @DescriptionJson, @IsRequired, @SettingsJson)",
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
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                for (var choiceIndex = 0; choiceIndex < question.Choices.Length; choiceIndex++)
                {
                    var choice = question.Choices[choiceIndex];

                    await connection.ExecuteAsync(Sql(
                        "INSERT INTO [QuestionChoices] ([ChoiceId], [SurveyId], "
                        + "[QuestionId], [SortOrder], [Value], [LabelJson], "
                        + "[IsOther]) "
                        + "VALUES (@ChoiceId, @SurveyId, @QuestionId, @SortOrder, @Value, "
                        + "@LabelJson, @IsOther)",
                        new
                        {
                            ChoiceId = Guid.NewGuid(),
                            SurveyId = surveyId,
                            question.QuestionId,
                            SortOrder = choiceIndex,
                            choice.Value,
                            LabelJson = WriteText(choice.Label),
                            choice.IsOther,
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
