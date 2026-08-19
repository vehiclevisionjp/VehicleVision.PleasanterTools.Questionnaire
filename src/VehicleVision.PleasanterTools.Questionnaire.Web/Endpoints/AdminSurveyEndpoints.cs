using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

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

        // **管理操作を残す**（Issue #19）。読み取りは残さない
        group.AddEndpointFilter<AuditLogFilter>();

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
            HttpContext context,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyTitleRequired, RequestLanguage.Of(context)),
                });
            }

            if (request.PleasanterSiteId <= 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PleasanterSiteIdRequired, RequestLanguage.Of(context)),
                });
            }

            var surveyId = Guid.NewGuid();
            var record = new SurveyRecord(
                surveyId,
                SurveyPublicId.Generate(),
                request.Title.Trim(),
                request.PleasanterSiteId,
                request.ResponseJsonColumn,
                (int)SurveyStatus.Draft,
                PublishedVersion: null);

            await surveys.SaveAsync(record, cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/admin/surveys/{surveyId}", new { surveyId, record.PublicId });
        });

        // ---- 複製（Administrator だけ） --------------------------------------
        // **設問・選択肢・ページ・分岐・マッピングを写し、下書きとして作る**（Issue #46）。
        // 似たアンケートを作り直すたびに手で入れ直さずに済ませる。
        // **Editor には行わせない。** 書き込み先のサイトを新しく決める操作であり、
        // 誤ると別の業務のサイトへ回答が流れ込む
        group.MapPost("/{surveyId:guid}/duplicate", async (
            Guid surveyId,
            DuplicateSurveyRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            if (request.PleasanterSiteId <= 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PleasanterSiteIdRequired, RequestLanguage.Of(context)),
                });
            }

            var source = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (source is null)
            {
                return Results.NotFound();
            }

            // **テンプレートは複製の口から作らせない**（Issue #58）。
            // テンプレートはサイトを持たず、題名に「のコピー」も付けたくない。
            // 専用の口（`AdminTemplateEndpoints`）を通させる
            if (source.IsTemplate)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
                });
            }

            // **元と同じサイトを断る。** 1 アンケート = 1 サイトなので、
            // 同じサイトへ 2 つのアンケートが書き込むと、回答がどちらのものか分からなくなる
            if (source.PleasanterSiteId == request.PleasanterSiteId)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.DuplicateSiteIdMustDiffer, RequestLanguage.Of(context)),
                });
            }

            var target = new SurveyDuplicationTarget(
                Guid.NewGuid(),
                // **公開用 ID は使い回さない**（_documents/データモデル設計.md 3 章）
                SurveyPublicId.Generate(),
                request.PleasanterSiteId,
                string.IsNullOrWhiteSpace(request.ResponseJsonColumn)
                    ? null
                    : request.ResponseJsonColumn.Trim());

            // **失敗したら 1 行も残さない。** 中途半端な行は画面からも消せない
            var duplicated = await drafts.DuplicateAsync(surveyId, target, cancellationToken)
                .ConfigureAwait(false);

            return duplicated
                ? Results.Created(
                    $"/api/admin/surveys/{target.SurveyId}",
                    new { surveyId = target.SurveyId, target.PublicId })
                : Results.NotFound();
        }).RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

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
            HttpContext context,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            if (request.Definition is null || request.Mapping is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.DefinitionAndMappingRequired, RequestLanguage.Of(context)),
                });
            }

            // **色は形を検査してからでないと通さない**（Issue #56）。
            // 画面の色欄は `#rrggbb` しか作れないので、ここへ形の違う値が来るのは
            // **画面を通さずに送られたとき。** 黙って捨てず、断って気付かせる。
            // **保存させない側に倒す。** 設問の不備（公開のときに弾く）と違い、
            // これは直しようのある入力の誤りではなく、**画面が作り得ない値**
            var invalidColors = request.Definition.Theme?.InvalidColors() ?? [];
            if (!invalidColors.IsEmpty)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.ThemeColorInvalid, RequestLanguage.Of(context)),
                    fields = invalidColors,
                });
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
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyUpdatedByOther, RequestLanguage.Of(context)),
                    actualRevision = exception.Actual,
                });
            }
        });

        // ---- ヘッダ画像（Issue #56） ------------------------------------------
        // **回答の添付と同じ道を通す**（拡張子・先頭バイト・大きさ）。
        // 管理者が上げるものでも緩めない。**公開アンケートを見た全員へ配るファイル**なので、
        // 乗っ取られた 1 つの管理者の口が、そのまま配布の口になる
        group.MapPost("/{surveyId:guid}/theme/header-image", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyAssetStore assets,
            CancellationToken cancellationToken) =>
        {
            var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (survey is null)
            {
                return Results.NotFound();
            }

            // **要求本文の上限を明示する。既定値に任せない**（_documents/非機能設計.md 1 章）
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = HeaderImage.MaxRequestBodyBytes;
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.HeaderImageRejected, RequestLanguage.Of(context)),
                });
            }

            IFormFile? file;
            try
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                file = form.Files.GetFile("image") ?? form.Files.FirstOrDefault();
            }
            catch (BadHttpRequestException exception)
            {
                // 上限超過（413）と壊れた本文（400）を、そのままの意味で返す
                return Results.StatusCode(exception.StatusCode);
            }

            if (file is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.HeaderImageRejected, RequestLanguage.Of(context)),
                });
            }

            using var buffer = new MemoryStream((int)Math.Max(file.Length, 0));
            await file.CopyToAsync(buffer, cancellationToken);

            // **ファイル名は名乗ったまま渡す。** ここで整えると、
            // パス区切りを含む名前を弾く検査が働かなくなる
            var incoming = new IncomingAttachment(file.FileName, buffer.ToArray());
            var rejections = await HeaderImage.InspectAsync(incoming, cancellationToken);

            // **配信する型は、検査を通った拡張子からこちらで決める。**
            // ブラウザが名乗った Content-Type は使わない
            var contentType = HeaderImage.ContentTypeOf(file.FileName);

            if (!rejections.IsEmpty || contentType is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.HeaderImageRejected, RequestLanguage.Of(context)),
                    reasons = rejections.Select(rejection => rejection.Reason.ToString()),
                });
            }

            // **行は上書きしない。** 差し替えても、公開済みの版が指している画像は変わらない
            var assetId = await assets.AddAsync(
                surveyId, contentType, file.FileName, incoming.Content.ToArray(), cancellationToken)
                .ConfigureAwait(false);

            // **下書きへは書かない。** 定義の保存は下書きの版と照合して行う決まりなので、
            // ここで書くと版が合わずに黙った上書きになる。**画面が定義へ入れて保存する**
            return Results.Ok(new { assetId });
        });

        // **編集中の画像を管理画面へ返す。** 公開前の画像は回答画面の口からは出ない
        group.MapGet("/{surveyId:guid}/assets/{assetId:guid}", async (
            Guid surveyId,
            Guid assetId,
            ISurveyAssetStore assets,
            CancellationToken cancellationToken) =>
        {
            var asset = await assets.FindAsync(surveyId, assetId, cancellationToken)
                .ConfigureAwait(false);

            // **配る前に型を確かめる。** 画像以外を自分のドメインから配らない
            return asset is null || !HeaderImage.IsAllowedContentType(asset.ContentType)
                ? Results.NotFound()
                : Results.File(asset.Content, asset.ContentType);
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

            var problems = MappingValidator.Validate(
                draft.Mapping, draft.Definition, null, PleasanterColumn.IsAttachment);
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

            // **テンプレートは公開できない**（Issue #58）。
            // 書き込み先のサイトを持たないので、公開できてしまうと
            // 受け付けた回答の行き先が無いまま溜まる。
            // **ここで 1 回引くのは、公開の後に引き直す分とは別。**
            // 後の引き直しは版が進んだ行を読むためのもので、前倒しにできない
            var beforePublish = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (beforePublish is { IsTemplate: true })
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
                });
            }

            // **公開のときだけ拒否する。** 壊れた定義で回答を受け付けると、
            // 受け付けた回答が Pleasanter へ届かないまま溜まる
            var problems = MappingValidator.Validate(
                draft.Mapping, draft.Definition, null, PleasanterColumn.IsAttachment);
            var blocking = problems.Where(problem => problem.IsBlocking).ToList();
            if (blocking.Count > 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PublishBlockedByMapping, RequestLanguage.Of(context)),
                    problems = blocking.Select(Describe),
                });
            }

            // **分岐が壊れたまま公開しない**（Issue #41）。
            // 無限に回る・辿り着けないページがある、といった不備は
            // **公開してからでは回答者にしか見えない**
            var flowProblems = SurveyFlowValidator.Validate(draft.Definition);
            if (flowProblems.Length > 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PublishBlockedByFlow, RequestLanguage.Of(context)),
                    flow = flowProblems.Select(problem => new
                    {
                        code = problem.Code.ToString(),
                        pageId = problem.PageId,
                        questionId = problem.QuestionId,
                        detail = problem.Detail,
                    }),
                });
            }

            if (draft.Definition.AllQuestions.All(question => question.IsDisplayOnly))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.NoAnswerableQuestion, RequestLanguage.Of(context)),
                });
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
            catch (InvalidOperationException)
            {
                // **版は不変。** 同時に 2 人が公開を押した場合など。
                // **例外の文言をそのまま返さない。** 内部の事情を管理画面へ出さないためと、
                // 文言が日本語で焼き付いていて英語にできないため
                return Results.Conflict(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.VersionAlreadyPublished, RequestLanguage.Of(context)),
                });
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

        // ---- 公開設定 --------------------------------------------------------
        // **回答数の上限を編集できるようにする**（Issue #53）。
        // 列はあったが、どこからも書けなかった
        group.MapPut("/{surveyId:guid}/settings", async (
            Guid surveyId,
            SurveySettingsRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            // **0 と負の数を断る。** 0 を上限として保存すると、
            // 公開しているのに誰も回答できないアンケートができる。
            // 上限を外したいときは null を送ってもらう
            if (request.ResponseLimit is { } limit && limit <= 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.ResponseLimitMustBePositive, RequestLanguage.Of(context)),
                });
            }

            var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            // **上限を引き上げても勝手に再開しない**（_documents/データモデル設計.md 2.1）。
            // 再開するかどうかは人が決める
            await surveys
                .SaveAsync(record with { ResponseLimit = request.ResponseLimit }, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { responseLimit = request.ResponseLimit });
        });

        // ---- 停止と再開 ------------------------------------------------------
        group.MapPost("/{surveyId:guid}/suspend", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            IResponseTokenStore tokens,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(
                surveyId, SurveyStatus.Suspended, context, surveys, tokens, cancellationToken));

        group.MapPost("/{surveyId:guid}/resume", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            IResponseTokenStore tokens,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(
                surveyId, SurveyStatus.Published, context, surveys, tokens, cancellationToken));

        return builder;
    }

    /// <summary>停止と再開。**理由を必ず書き換える。**</summary>
    /// <remarks>
    /// **手で止めたのか、上限で自動停止したのかを残す**
    /// （<c>_documents/データモデル設計.md</c> 2.1）。
    /// 残さないと、管理画面で「なぜ止まっているのか」が分からない。
    /// </remarks>
    private static async Task<IResult> ChangeStatusAsync(
        Guid surveyId,
        SurveyStatus status,
        HttpContext context,
        ISurveyRepository surveys,
        IResponseTokenStore tokens,
        CancellationToken cancellationToken)
    {
        var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Results.NotFound();
        }

        // **テンプレートには状態が無い**（Issue #58）。公開しないので止める対象でもない
        if (record.IsTemplate)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
            });
        }

        // **公開していないものは再開できない。** 版が無いので回答画面が組み立てられない
        if (status is SurveyStatus.Published && record.PublishedVersion is null)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.NotPublishedYet, RequestLanguage.Of(context)),
            });
        }

        // **上限に達したまま再開させない。** 再開しても最初の回答で再び自動停止するだけで、
        // 押した人には「再開できたのに止まっている」としか見えない。
        // **先に上限を引き上げてもらう**
        if (status is SurveyStatus.Published && record.ResponseLimit is { } limit && limit > 0)
        {
            var accepted = await tokens.CountAcceptedAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);

            if (accepted >= limit)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.ResponseLimitReached,
                        RequestLanguage.Of(context),
                        accepted,
                        limit),
                });
            }
        }

        var updated = status is SurveyStatus.Suspended
            ? record with
            {
                Status = (int)status,
                SuspendedReason = (int)SurveySuspendedReason.Manual,
                // **DB へ入れる時刻は必ず DbTime を通す**（秒未満と Kind の落とし穴）
                SuspendedAt = DbTime.UtcNowTruncated(),
            }
            // **再開したら理由を消す。** 残すと、次に一覧を見た人が
            // 動いているアンケートに停止の理由が付いているのを見ることになる
            : record with { Status = (int)status, SuspendedReason = null, SuspendedAt = null };

        await surveys.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

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

    /// <summary>アンケートを新しく作る。</summary>
    public sealed record CreateSurveyRequest(
        string? Title,
        long PleasanterSiteId,
        string? ResponseJsonColumn);

    /// <summary>アンケートを複製する（Issue #46）。</summary>
    /// <param name="PleasanterSiteId">
    /// **複製先が書き込むサイト。** 元の値を写さないので、必ず指定させる。
    /// </param>
    /// <param name="ResponseJsonColumn">
    /// 回答 JSON の正本を入れる列。**サイトに紐づく値なので写さない。**
    /// </param>
    public sealed record DuplicateSurveyRequest(
        long PleasanterSiteId,
        string? ResponseJsonColumn);

    /// <summary>公開設定の保存（Issue #53）。</summary>
    /// <param name="ResponseLimit">
    /// 受け付ける回答の上限。**<c>null</c> は「上限なし」。**
    /// </param>
    public sealed record SurveySettingsRequest(int? ResponseLimit);

    /// <summary>下書きの保存。</summary>
    /// <param name="Revision">読んだときの版。**これが今の版と違えば拒否する。**</param>
    public sealed record SaveDraftRequest(
        SurveyDefinition? Definition,
        MappingDefinition? Mapping,
        int Revision);
}
