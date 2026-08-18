using System.Security.Claims;
using System.Security.Cryptography;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>アンケートの作成と編集の入口。</summary>
/// <remarks>
/// <para>
/// **すべて <see cref="AdminAuthSchemes.Session"/> が要る。**
/// 合言葉だけ通した途中状態では操作させない。
/// </para>
/// <para>
/// **編集は公開へ影響しない。** 下書きを保存しても回答画面は変わらず、
/// <c>publish</c> で版を固めて初めて反映される
/// （<c>_documents/データモデル設計.md</c> 1 章）。
/// </para>
/// </remarks>
public static class AdminSurveyEndpoints
{
    public static IEndpointRouteBuilder MapAdminSurveyEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/surveys")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(AdminAuthSchemes.Session)
                .RequireAuthenticatedUser());

        // **管理画面の応答を途中の経路に残さない**
        group.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            return await next(context);
        });

        // ---- 一覧 ------------------------------------------------------------
        group.MapGet("/", async (ISurveyDraftStore drafts, CancellationToken cancellationToken) =>
            Results.Ok(await drafts.ListAsync(cancellationToken).ConfigureAwait(false)));

        // ---- 作成 ------------------------------------------------------------
        group.MapPost("/", async (
            CreateSurveyRequest request,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { message = "題名を入力してください。" });
            }

            if (request.PleasanterSiteId <= 0)
            {
                return Results.BadRequest(new { message = "Pleasanter のサイト ID を指定してください。" });
            }

            var surveyId = Guid.NewGuid();
            var record = new SurveyRecord(
                surveyId,
                GeneratePublicId(),
                request.Title.Trim(),
                request.PleasanterSiteId,
                request.ResponseJsonColumn,
                (int)SurveyStatus.Draft,
                PublishedVersion: null);

            await surveys.SaveAsync(record, cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/admin/surveys/{surveyId}", new { surveyId, record.PublicId });
        });

        // ---- 下書きを読む ----------------------------------------------------
        group.MapGet("/{surveyId:guid}", async (
            Guid surveyId,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        });

        // ---- 下書きを保存する ------------------------------------------------
        group.MapPut("/{surveyId:guid}", async (
            Guid surveyId,
            SaveDraftRequest request,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            if (request.Definition is null || request.Mapping is null)
            {
                return Results.BadRequest(new { message = "定義とマッピングの両方が要ります。" });
            }

            // **不備があっても保存はさせる。** 直している途中で保存できないと作業にならない。
            // **拒否するのは公開のとき**
            try
            {
                var revision = await drafts.SaveAsync(
                    surveyId, request.Definition, request.Mapping, request.Revision, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(new { revision });
            }
            catch (SurveyDraftConflictException exception)
            {
                // **黙って上書きしない。** 読み直させる
                return Results.Conflict(new
                {
                    message = "他の人がこのアンケートを更新しました。読み直してください。",
                    actualRevision = exception.Actual,
                });
            }
        });

        // ---- 公開前の検査 ----------------------------------------------------
        group.MapGet("/{surveyId:guid}/problems", async (
            Guid surveyId,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                return Results.NotFound();
            }

            var problems = MappingValidator.Validate(draft.Mapping, draft.Definition);
            return Results.Ok(problems.Select(Describe));
        });

        // ---- 公開 ------------------------------------------------------------
        group.MapPost("/{surveyId:guid}/publish", async (
            Guid surveyId,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                return Results.NotFound();
            }

            // **公開のときだけ拒否する。** 壊れた定義で回答を受け付けると、
            // 受け付けた回答が Pleasanter へ届かないまま溜まる
            var problems = MappingValidator.Validate(draft.Mapping, draft.Definition);
            var blocking = problems.Where(problem => problem.IsBlocking).ToList();
            if (blocking.Count > 0)
            {
                return Results.BadRequest(new
                {
                    message = "公開できません。マッピングの不備を直してください。",
                    problems = blocking.Select(Describe),
                });
            }

            if (draft.Definition.AllQuestions.All(question => question.IsDisplayOnly))
            {
                return Results.BadRequest(new { message = "回答できる設問がありません。" });
            }

            var publishedBy = context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id
                && Guid.TryParse(id, out var adminUserId)
                ? adminUserId
                : (Guid?)null;

            try
            {
                await surveys.PublishAsync(
                    surveyId,
                    draft.Definition.Version,
                    draft.Definition,
                    draft.Mapping,
                    publishedBy,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                // **版は不変。** 同時に 2 人が公開を押した場合など
                return Results.Conflict(new { message = exception.Message });
            }

            var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (record is not null)
            {
                await surveys.SaveAsync(
                    record with { Status = (int)SurveyStatus.Published }, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.Ok(new
            {
                version = draft.Definition.Version,
                // **未割り当ては拒否しないが、公開後も伝える**
                warnings = problems.Where(problem => !problem.IsBlocking).Select(Describe),
            });
        });

        // ---- 停止と再開 ------------------------------------------------------
        group.MapPost("/{surveyId:guid}/suspend", (
            Guid surveyId,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(surveyId, SurveyStatus.Suspended, surveys, cancellationToken));

        group.MapPost("/{surveyId:guid}/resume", (
            Guid surveyId,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(surveyId, SurveyStatus.Published, surveys, cancellationToken));

        return builder;
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid surveyId,
        SurveyStatus status,
        ISurveyRepository surveys,
        CancellationToken cancellationToken)
    {
        var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Results.NotFound();
        }

        // **公開していないものは再開できない。** 版が無いので回答画面が組み立てられない
        if (status is SurveyStatus.Published && record.PublishedVersion is null)
        {
            return Results.BadRequest(new { message = "まだ公開されていません。" });
        }

        await surveys.SaveAsync(record with { Status = (int)status }, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new { status = status.ToString() });
    }

    /// <summary>不備を画面に出せる形にする。</summary>
    private static object Describe(MappingProblem problem) => new
    {
        code = problem.Code.ToString(),
        targetColumn = problem.TargetColumn,
        detail = problem.Detail,
        isBlocking = problem.IsBlocking,
    };

    /// <summary>回答用 URL に使う推測不能な値。</summary>
    /// <remarks>
    /// **サイト ID や <c>SurveyId</c> を URL に出さない**
    /// （<c>_documents/データモデル設計.md</c> 3 章）。
    /// 順番に並んだ値だと、総当たりで他のアンケートを見つけられる。
    /// </remarks>
    private static string GeneratePublicId() =>
        "pub-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>アンケートを新しく作る。</summary>
    public sealed record CreateSurveyRequest(
        string? Title,
        long PleasanterSiteId,
        string? ResponseJsonColumn);

    /// <summary>下書きの保存。</summary>
    /// <param name="Revision">読んだときの版。**これが今の版と違えば拒否する。**</param>
    public sealed record SaveDraftRequest(
        SurveyDefinition? Definition,
        MappingDefinition? Mapping,
        int Revision);
}
