using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Flow;
using VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>アンケートの作成と編集の入口。</summary>
/// <remarks>
/// <para>
/// **すべて <see cref="AdminAuthSchemes.Session"/> が要る。**
/// パスワードだけ通した途中状態では操作させない。
/// </para>
/// <para>
/// **編集は公開へ影響しない。** 下書きを保存しても回答画面は変わらず、
/// <c>publish</c> で版を固めて初めて反映される
/// （<c>_documents/データモデル設計.md</c> 1 章）。
/// </para>
/// </remarks>
public static class AdminSurveyEndpoints
{
    /// <summary>1 度に返す上限。**画面から大きな値を指定されても超えない。**</summary>
    private const int MaxListLimit = 200;

    /// <summary>作成したときに置く、空のページの ID（Issue #429）。</summary>
    /// <remarks>
    /// **画面が足すページと同じ書き方にそろえる**（<c>page-</c> で始まる）。
    /// ⚠️ **固定の値にしない。** 同じ ID が複数のアンケートに出ると、
    /// 分岐の行き先をコピーしたときに取り違える
    /// </remarks>
    private static string InitialPageId => $"page-{Guid.NewGuid():N}"[..13];

    private const int DefaultListLimit = 50;

    public static IEndpointRouteBuilder MapAdminSurveyEndpoints(this IEndpointRouteBuilder builder)
    {
        // **群には「閲覧」を掛ける**（Issue #160）。書き込み・公開は口ごとに足す
        var group = builder.MapGroup("/api/admin/surveys")
            .WithTags("管理 API")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysRead));

        // **管理操作を残す**（Issue #19）。読み取りは残さない
        group.AddEndpointFilter<AuditLogFilter>();

        // **Pleasanter への接続先（BaseUrl）の設定が使えない。** 認証の失敗でも恒久的な失敗でもなく、サーバー側の設定の誤り（内部エラー）。
        // 固定の文言だけを返す（URL・列名・API キーを含めない）
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (PleasanterConfigurationException)
            {
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.PleasanterConfigurationInvalid,
                            RequestLanguage.Of(context.HttpContext)),
                    },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // **管理画面の応答を途中の経路に残さない**
        group.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            return await next(context);
        });

        // ---- 埋め込みを許す配信元（Issue #104 / #107） -------------------------
        // **管理画面に「何が許されているか」を出すため。**
        // 出さないと、保存して断られるまで分からず、書き直しの繰り返しになる。
        // **設定そのもの（既に運用側が知っている値）なので、管理者へ見せて困らない**
        group.MapGet("/embed-options", async (
            IAppSettingsProvider provider,
            CancellationToken cancellationToken) =>
        {
            var embeds = EmbedOptions.FromSnapshot(
                await provider.GetAsync(cancellationToken).ConfigureAwait(false));
            return Results.Ok(new
            {
                enabled = embeds.Enabled,
                allowedHosts = embeds.AllowedHosts,
            });
        });

        group.MapGet("/embed-parent-options", async (
            IAppSettingsProvider provider,
            CancellationToken cancellationToken) =>
        {
            var parents = EmbedParentOptions.FromSnapshot(
                await provider.GetAsync(cancellationToken).ConfigureAwait(false));
            return Results.Ok(new
            {
                enabled = parents.Enabled,
                allowedParents = parents.AllowedParents,
            });
        });

        // ---- 一覧 ------------------------------------------------------------
        // **全件は返さない**（Issue #79）。アンケートは消さずに溜まるので、
        // 上限が無いと増えるほど画面が重くなり、探すこともできない。
        // **総数は数えず、1 件多く読んで「次がある」だけを返す**（デッドレターと同じ形）
        group.MapGet("/", async (
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken,
            int? limit = null,
            int? offset = null,
            string? title = null,
            int? status = null,
            bool includeArchived = false) =>
        {
            var take = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);

            // **知らない状態は絞り込みとして使わない。** 静かに 0 件になると
            // 「アンケートが消えた」と見える
            if (status is { } value && !Enum.IsDefined(typeof(SurveyStatus), value))
            {
                return Results.BadRequest(new { error = "unknownStatus" });
            }

            var rows = await drafts.ListAsync(
                new SurveyListQuery
                {
                    Limit = take + 1,
                    Offset = Math.Max(offset ?? 0, 0),
                    TitleContains = title,
                    Status = status is { } known ? (SurveyStatus)known : null,
                    IncludeArchived = includeArchived,
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(ToResponse(rows, take));
        })
            .AllowAutomation(AdminPermissions.SurveysRead);

        // ---- 作成 ------------------------------------------------------------
        group.MapPost("/", async (
            CreateSurveyRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyDraftStore drafts,
            PleasanterGateway pleasanter,
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

            var pleasanterSiteId = request.PleasanterSiteId;
            if (request.CreatePleasanterSite)
            {
                var created = await pleasanter.CreateSiteAsync(
                    request.ParentPleasanterSiteId,
                    new Dictionary<string, object?>
                    {
                        ["Title"] = request.Title.Trim(),
                        ["ReferenceType"] = "Results",
                        ["SiteSettings"] = new JsonObject(),
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!created.IsSuccess || created.Id is not { } createdSiteId)
                {
                    return PleasanterFailure(created, context);
                }

                pleasanterSiteId = createdSiteId;
            }
            else if (pleasanterSiteId <= 0)
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
                pleasanterSiteId,
                request.ResponseJsonColumn,
                (int)SurveyStatus.Draft,
                PublishedVersion: null);

            await surveys.SaveAsync(record, cancellationToken).ConfigureAwait(false);

            // **空のページを 1 つ持たせて始める**（Issue #429）。
            // **1 ページも無いアンケートに意味は無い**ので、作った直後に必ず要る一手を省く。
            // ⚠️ **見出しは入れない。** 「1 ページ目」のような文言を置くと、消す手間になる。
            // 設問は 0 件なので、**この状態では公開できない**（公開前の検査で弾かれる）。
            await drafts.SaveAsync(
                surveyId,
                new SurveyDefinition
                {
                    SurveyId = surveyId.ToString(),
                    Version = 1,
                    Title = LocalizedText.Japanese(request.Title.Trim()),
                    Pages = [new Page { PageId = InitialPageId }],
                },
                new MappingDefinition(),
                expectedRevision: 0,
                cancellationToken).ConfigureAwait(false);

            return Results.Created($"{context.Request.PathBase}/api/admin/surveys/{surveyId}", new { surveyId, record.PublicId });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // ---- 複製（Administrator だけ） --------------------------------------
        // **設問・選択肢・ページ・分岐・マッピングをコピーし、下書きとして作る**（Issue #46）。
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

            if (source.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
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
                    $"{context.Request.PathBase}/api/admin/surveys/{target.SurveyId}",
                    new { surveyId = target.SurveyId, target.PublicId })
                : Results.NotFound();
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        // ---- 書き出し（Issue #1009） -----------------------------------------
        // **定義とマッピングだけを JSON にして返す。** 書き込み先・公開用 ID・公開状態・
        // 資格情報・画像は入れない（`SurveyExchange`）。**秘密を持たないので、
        // 閲覧できる人なら書き出せる**（画面で見えるものと同じ）
        group.MapGet("/{surveyId:guid}/export", async (
            Guid surveyId,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                return Results.NotFound();
            }

            var json = SurveyExchange.Export(draft.Definition, draft.Mapping);
            return Results.File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                // **題名をファイル名へ入れない。** 日本語や記号が環境ごとに化ける
                fileDownloadName: $"survey-{surveyId:N}.json");
        })
            // **トークンでの書き出しは監査へ残す**（定義の一括持ち出し。#1027）。人の画面の読み取りは残さない
            .AuditAutomationReads()
            // **閲覧と同じ `surveys.read` でトークンを許す**（スコープは新設しない。#1029）
            .AllowAutomation(AdminPermissions.SurveysRead)
            // OpenAPI の説明（#1030）。トークンで呼べること・スコープは、許可宣言から生成される
            .WithDescription("アンケートの定義とマッピングを JSON で書き出す（書き込み先・公開用 ID・公開状態・資格情報・画像は入れない）。");

        // ---- 取り込み（公開と同じ権限） --------------------------------------
        // **新しいアンケートの下書きとして作る。** 複製と同じく、書き込み先のサイトを
        // 新しく決める操作なので、公開と同じ権限を要る。
        // **公開前の検査と同じ検査を通す**（`PublishInspector`）。通らなければ 1 行も作らない
        group.MapPost("/import", async (
            ImportSurveyRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyDraftStore drafts,
            IAppSettingsProvider appSettings,
            CancellationToken cancellationToken) =>
        {
            if (request.Document.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return ImportInvalid(context, SurveyExchangeError.Malformed);
            }

            var parsed = SurveyExchange.Parse(request.Document.GetRawText());
            if (parsed.Document is not { } document)
            {
                return ImportInvalid(context, parsed.Error ?? SurveyExchangeError.Malformed, parsed.Detail);
            }

            var surveyId = Guid.NewGuid();
            var definition = SurveyExchange.ToDraft(document, surveyId.ToString());

            // **保存の関所を迂回させない**（フォールバック言語・色・埋め込み先）
            var embeds = EmbedOptions.FromSnapshot(
                await appSettings.GetAsync(cancellationToken).ConfigureAwait(false));
            if (DefinitionSaveRules.Check(definition, embeds) is { } violation)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(violation.MessageKey, RequestLanguage.Of(context)),
                    fields = violation.Fields,
                });
            }

            // **ルックアップの設定を持ち込めるのはアンケート管理者だけ**（保存と同じ。Issue #879）。
            // **トークンでは持ち込めない**（`lookups.manage` はスコープに付けられない。#1029）。
            // 無視して取り込まず、403 で断る（黙って落とすと、設定が入ったつもりになる）。
            // トークンの principal は役割の claim を持たないので役割の判定でも落ちるが、
            // 役割の持たせ方が変わっても開かないよう、トークンかどうかを明示して見る
            if (!definition.GetLookups().IsEmpty
                && (AdminApiTokenAuthentication.IsApiToken(context.User)
                    || !AdminPermissions.Has(
                        context.User.FindFirstValue(ClaimTypes.Role), AdminPermissions.LookupsManage)))
            {
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.LookupSettingsForbidden, RequestLanguage.Of(context)),
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var inspection = PublishInspector.Inspect(definition, document.Mapping);
            var report = new
            {
                mapping = inspection.MappingProblems.Where(problem => problem.IsBlocking).Select(Describe),
                warnings = inspection.MappingProblems.Where(problem => !problem.IsBlocking).Select(Describe),
                flow = inspection.Flow.Select(problem => new
                {
                    code = problem.Code.ToString(),
                    pageId = problem.PageId,
                    questionId = problem.QuestionId,
                    detail = problem.Detail,
                }),
                autoReply = inspection.AutoReply.Select(problem => new
                {
                    code = problem.Code.ToString(),
                    detail = problem.Detail,
                }),
                settings = inspection.Settings.Select(problem => new
                {
                    code = problem.Code.ToString(),
                    questionId = problem.QuestionId,
                    detail = problem.Detail,
                }),
                lookups = inspection.LookupStructure.Select(problem => new
                {
                    lookupId = problem.LookupId,
                    code = problem.Code.ToString(),
                    detail = problem.Detail,
                }),
                assetTicketDaysInvalid = inspection.AssetTicketDaysInvalid,
                noAnswerableQuestion = inspection.NoAnswerableQuestion,
                // **画像は運ばない。** 記法が指す画像は取り込み先に無いので、知らせる
                omittedAssetReferences = SurveyAssetReferences.ReferencedAssetIds(definition).Count,
            };

            if (inspection.HasBlocking)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyImportBlocked, RequestLanguage.Of(context)),
                    problems = report,
                });
            }

            // **検査だけ**（取り込む前の確認）。何も作らない
            if (request.DryRun)
            {
                return Results.Ok(new { valid = true, problems = report });
            }

            if (request.PleasanterSiteId <= 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.PleasanterSiteIdRequired, RequestLanguage.Of(context)),
                });
            }

            var title = definition.Title.Get(LocalizedText.DefaultLanguage).Trim();
            var record = new SurveyRecord(
                surveyId,
                // **公開用 ID は新しく作る**（_documents/データモデル設計.md 3 章）
                SurveyPublicId.Generate(),
                title.Length == 0 ? surveyId.ToString() : title,
                request.PleasanterSiteId,
                string.IsNullOrWhiteSpace(request.ResponseJsonColumn)
                    ? null
                    : request.ResponseJsonColumn.Trim(),
                (int)SurveyStatus.Draft,
                PublishedVersion: null);

            await surveys.SaveAsync(record, cancellationToken).ConfigureAwait(false);
            await drafts.SaveAsync(
                surveyId, definition, document.Mapping, expectedRevision: 0, cancellationToken)
                .ConfigureAwait(false);

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            return Results.Created(
                $"{context.Request.PathBase}/api/admin/surveys/{surveyId}",
                new { surveyId, record.PublicId, problems = report });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            // **取り込みが今要る権限（公開相当 `surveys.publish`）と同じスコープでトークンを許す**（#1029）。
            // ⚠️ 宣言は口の権限（RequireAuthorization）と同じにする。ずらすと、別のスコープのトークンが通ってしまう
            .AllowAutomation(AdminPermissions.SurveysPublish)
            // OpenAPI の説明（#1030）。トークンで呼べること・スコープは、許可宣言から生成される
            .WithDescription(
                "書き出した JSON から、新しいアンケートの下書きを作る。公開前の検査と同じ検査を通らなければ 1 行も作らない。"
                + "ルックアップの設定を含む取り込みは、個人トークンでは 403（lookups.manage はスコープに付けられない。"
                + "検査だけ（dryRun）の要求でも断る）。人のセッションでは、役割に lookups.manage があれば持ち込める。")
            .AddEndpointFilter(async (filterContext, next) =>
            {
                // **大きさを入口で絞る。** 既定の上限のまま JSON を読み切らない
                var feature = filterContext.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = SurveyExchange.MaxBytes + (64 * 1024);
                }

                return await next(filterContext);
            });

        // ---- 下書きを読む ----------------------------------------------------
        group.MapGet("/{surveyId:guid}", async (
            Guid surveyId,
            ISurveyDraftStore drafts,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        })
            .AllowAutomation(AdminPermissions.SurveysRead);

        // ---- 固めた版を読む（Issue #544）--------------------------------------
        // **読むだけ。** 版は不変なので、ここからは何も書き換えない。
        // 編集へ読み込んで保存するかどうかは、画面側で利用者が決める
        group.MapGet("/{surveyId:guid}/versions", async (
            Guid surveyId,
            ISurveySnapshotStore snapshots,
            CancellationToken cancellationToken) =>
            Results.Ok(await snapshots.ListAsync(surveyId, cancellationToken).ConfigureAwait(false)))
            .AllowAutomation(AdminPermissions.SurveysRead);

        group.MapGet("/{surveyId:guid}/versions/{version:int}", async (
            Guid surveyId,
            int version,
            ISurveySnapshotStore snapshots,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await snapshots.FindAsync(surveyId, version, cancellationToken)
                .ConfigureAwait(false);
            return snapshot is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    definition = snapshot.Definition,
                    mapping = snapshot.Mapping,
                    assetHistorySiteId = snapshot.AssetHistorySiteId,
                    assetHistoryMapping = snapshot.AssetHistoryMapping,
                });
        })
            .AllowAutomation(AdminPermissions.SurveysRead);

        // ---- ほかのアンケートから設問を取り込む（Issue #358）----------------
        // **読み取りにも取り込み先を含める。** URL の surveyId を変えて、
        // アーカイブ済みやテンプレートを編集先にできないようにする。
        group.MapGet("/{surveyId:guid}/question-import/{sourceSurveyId:guid}", async (
            Guid surveyId,
            Guid sourceSurveyId,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            var target = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            var source = await surveys.FindBySurveyIdAsync(sourceSurveyId, cancellationToken)
                .ConfigureAwait(false);
            if (!CanImportQuestions(target, source, surveyId, sourceSurveyId))
            {
                return QuestionImportSourceInvalid(context);
            }

            var draft = await drafts.LoadAsync(sourceSurveyId, cancellationToken)
                .ConfigureAwait(false);
            if (draft is null)
            {
                return QuestionImportSourceInvalid(context);
            }

            return Results.Ok(new QuestionImportSourceResponse(
            [
                .. draft.Definition.Pages.Select(page => new QuestionImportPageResponse(
                    page.PageId,
                    page.Title,
                    [
                        .. page.Questions.Select(question => new QuestionImportQuestionResponse(
                            question.QuestionId,
                            question.Type,
                            question.Title)),
                    ])),
            ]));
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        group.MapPost("/{surveyId:guid}/question-import/{sourceSurveyId:guid}", async (
            Guid surveyId,
            Guid sourceSurveyId,
            QuestionImportRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            var target = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            var source = await surveys.FindBySurveyIdAsync(sourceSurveyId, cancellationToken)
                .ConfigureAwait(false);
            if (!CanImportQuestions(target, source, surveyId, sourceSurveyId))
            {
                return QuestionImportSourceInvalid(context);
            }

            var requested = (request.QuestionIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            if (requested.Count == 0)
            {
                return QuestionImportSelectionRequired(context);
            }

            var sourceDraft = await drafts.LoadAsync(sourceSurveyId, cancellationToken)
                .ConfigureAwait(false);
            var targetDraft = await drafts.LoadAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (sourceDraft is null || targetDraft is null)
            {
                return QuestionImportSourceInvalid(context);
            }

            var questions = sourceDraft.Definition.AllQuestions
                .Where(question => requested.Contains(question.QuestionId))
                .ToList();
            if (questions.Count != requested.Count)
            {
                return QuestionImportSourceInvalid(context);
            }

            // **画面にまだ保存していない設問 ID も避ける。**
            // 連続して取り込んでも、次の保存まで ID が衝突しない。
            var existingQuestionIds = targetDraft.Definition.AllQuestions
                .Select(question => question.QuestionId)
                .Concat(request.ExistingQuestionIds ?? []);
            var imported = QuestionImport.Copy(
                questions,
                existingQuestionIds,
                () => $"q-{Guid.NewGuid():N}");

            return Results.Ok(imported);
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // ---- 実サイトの列数 --------------------------------------------------
        // **編集を開くときと、明示した取り直しだけで呼ぶ。** 入力のたびに Pleasanter へ
        // 問い合わせると、編集画面が外部サービスの遅延と可用性に引きずられる。
        group.MapGet("/{surveyId:guid}/column-availability", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            PleasanterGateway pleasanter,
            CancellationToken cancellationToken) =>
        {
            var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (survey is null)
            {
                return Results.NotFound();
            }

            if (survey.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            var response = await pleasanter.GetSiteAsync(survey.PleasanterSiteId, cancellationToken)
                .ConfigureAwait(false);
            var availableByPrefix = response.IsSuccess
                ? AvailableColumnsFrom(response.Body)
                : null;
            // **項目名・リンク・選択肢は、取れたものだけ添える。** 取れなくても編集は止めない
            var details = response.IsSuccess
                ? ColumnDetailsFrom(response.Body)
                : new Dictionary<string, ColumnDetail>();

            // **取得に失敗しても編集を止めない。** 項目拡張のない標準構成なら正しい本数であり、
            // 取得できないことを理由に、利用者が下書きを直せなくなる方を避ける。
            return Results.Ok(availableByPrefix is null
                ? new ColumnAvailabilityResponse("standard", new Dictionary<string, int>(), details)
                : new ColumnAvailabilityResponse("site", availableByPrefix, details));
        });

        // ---- マッピング先サイトの同期 ---------------------------------------
        group.MapPost("/{surveyId:guid}/site-settings/preview", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyDraftStore drafts,
            PleasanterGateway pleasanter,
            CancellationToken cancellationToken) =>
        {
            var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (survey is null || await drafts.LoadAsync(surveyId, cancellationToken)
                .ConfigureAwait(false) is not { } draft)
            {
                return Results.NotFound();
            }

            if (survey.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            var current = await pleasanter.GetSiteAsync(survey.PleasanterSiteId, cancellationToken)
                .ConfigureAwait(false);
            if (!current.IsSuccess || current.Body is null)
            {
                return PleasanterFailure(current, context);
            }

            if (SiteSettingsHidden(current.Body))
            {
                return SiteSettingsNotReadable(context);
            }

            try
            {
                var plan = SiteSettingsSynchronizer.Build(current.Body, draft.Mapping);
                return Results.Ok(new
                {
                    plan.AddedColumns,
                    plan.GridColumns,
                    plan.EditorColumns,
                    plan.HistoryColumns,
                    plan.Unchanged,
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        group.MapPost("/{surveyId:guid}/site-settings/sync", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyDraftStore drafts,
            PleasanterGateway pleasanter,
            CancellationToken cancellationToken) =>
        {
            var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (survey is null || await drafts.LoadAsync(surveyId, cancellationToken)
                .ConfigureAwait(false) is not { } draft)
            {
                return Results.NotFound();
            }

            if (survey.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            var current = await pleasanter.GetSiteAsync(survey.PleasanterSiteId, cancellationToken)
                .ConfigureAwait(false);
            if (!current.IsSuccess || current.Body is null)
            {
                return PleasanterFailure(current, context);
            }

            if (SiteSettingsHidden(current.Body))
            {
                return SiteSettingsNotReadable(context);
            }

            SiteSettingsSyncPlan plan;
            try
            {
                plan = SiteSettingsSynchronizer.Build(current.Body, draft.Mapping);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }

            var updated = await pleasanter.UpdateSiteAsync(
                survey.PleasanterSiteId,
                new Dictionary<string, object?> { ["SiteSettings"] = plan.SiteSettings },
                cancellationToken).ConfigureAwait(false);
            if (!updated.IsSuccess)
            {
                return PleasanterFailure(updated, context);
            }

            var after = await pleasanter.GetSiteAsync(survey.PleasanterSiteId, cancellationToken)
                .ConfigureAwait(false);
            if (!after.IsSuccess || after.Body is null)
            {
                return PleasanterFailure(after, context);
            }

            // ⚠️ **GetSite は Links を返さない**（実機で確認）。読み直しても成立は分からない。
            // Pleasanter の SetLinks は**相手サイトを引けるときだけ**リンクを作り、
            // **できなければ黙って捨てる**ので、その条件そのものを確かめる
            var linkedSiteIds = SiteSettingsSynchronizer.LinkedSiteIds(
                after.Body,
                draft.Mapping.Assignments.Select(assignment => assignment.TargetColumn));
            var unreachable = new List<long>();
            foreach (var linkedSiteId in linkedSiteIds)
            {
                var linked = await pleasanter.GetSiteAsync(linkedSiteId, cancellationToken)
                    .ConfigureAwait(false);
                if (!linked.IsSuccess)
                {
                    unreachable.Add(linkedSiteId);
                }
            }

            if (unreachable.Count > 0)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.LinkedSitesUnreachable, RequestLanguage.Of(context)),
                    unreachableSiteIds = unreachable,
                });
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "pleasanterSiteId", survey.PleasanterSiteId.ToString(CultureInfo.InvariantCulture));
            return Results.Ok(new { synchronized = true });
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // ---- 下書きを保存する ------------------------------------------------
        group.MapPut("/{surveyId:guid}", async (
            Guid surveyId,
            SaveDraftRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            IAppSettingsProvider appSettings,
            LookupPublishValidator lookupValidator,
            CancellationToken cancellationToken) =>
        {
            var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            if (record.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            if (request.Definition is null || request.Mapping is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.DefinitionAndMappingRequired, RequestLanguage.Of(context)),
                });
            }

            // **保存の関所は取り込み（Issue #1009）と共通。** フォールバック言語・色の形・
            // 許していない埋め込み先を、保存のときにその場で断る（`DefinitionSaveRules`）
            var embeds = EmbedOptions.FromSnapshot(
                await appSettings.GetAsync(cancellationToken).ConfigureAwait(false));
            if (DefinitionSaveRules.Check(request.Definition, embeds) is { } violation)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(violation.MessageKey, RequestLanguage.Of(context)),
                    fields = violation.Fields,
                });
            }

            // **ルックアップの設定を変えられるのはアンケート管理者だけ**（Issue #879・仕様書 U7）。
            // 編集者の保存では、ルックアップの定義を今の下書きから変えさせない（見る・他の項目を直すのは妨げない）
            if (!AdminPermissions.Has(
                context.User.FindFirstValue(ClaimTypes.Role), AdminPermissions.LookupsManage))
            {
                var current = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
                var before = SurveyJson.Serialize(current?.Definition.GetLookups() ?? []);
                var after = SurveyJson.Serialize(request.Definition.GetLookups());
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    return Results.Json(
                        new
                        {
                            message = ServerMessages.Get(
                                ServerMessageKeys.LookupSettingsForbidden, RequestLanguage.Of(context)),
                        },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }

            return await SaveDraftCoreAsync(
                surveyId, request, record, context, drafts, surveys, lookupValidator, cancellationToken)
                .ConfigureAwait(false);
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite))
            .AllowAutomation(AdminPermissions.SurveysWrite);

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
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // ---- 説明文と完了画面の配布資産（Issue #266 / #269）-----------------
        group.MapGet("/asset-options", (AssetOptions options) =>
            Results.Ok(new
            {
                allowedExtensions = options.AllowedExtensions,
                maxFileSizeBytes = options.MaxFileSizeBytes,
                maxFileCount = options.MaxFileCount,
            }));

        group.MapPost("/{surveyId:guid}/assets", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            ISurveyAssetStore assets,
            AssetOptions options,
            AssetInspector inspector,
            CancellationToken cancellationToken) =>
        {
            var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (survey is null)
            {
                return Results.NotFound();
            }

            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = options.MaxRequestBodyBytes;
            }

            if (!context.Request.HasFormContentType)
            {
                return ContentAssetRejected(context);
            }

            IFormFile? file;
            try
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                file = form.Files.GetFile("asset") ?? form.Files.FirstOrDefault();
            }
            catch (BadHttpRequestException exception)
            {
                return Results.StatusCode(exception.StatusCode);
            }

            if (file is null)
            {
                return ContentAssetRejected(context);
            }

            using var buffer = new MemoryStream((int)Math.Max(file.Length, 0));
            await file.CopyToAsync(buffer, cancellationToken);
            var incoming = new IncomingAttachment(file.FileName, buffer.ToArray());
            // **回答添付と同じ検査器を通す。** 管理者の口を奪われても、
            // 未検査のファイルを信頼されたドメインから配らせない
            var rejections = await inspector.InspectAsync(incoming, cancellationToken);
            var contentType = options.ContentTypeOf(file.FileName);
            if (rejections.Any(
                rejection => rejection.Reason is AttachmentRejectionReason.ScannerUnavailable))
            {
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.AssetScannerUnavailable,
                            RequestLanguage.Of(context)),
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!rejections.IsEmpty || contentType is null)
            {
                return ContentAssetRejected(
                    context,
                    rejections.Select(rejection => rejection.Reason.ToString()));
            }

            var assetId = await assets.TryAddContentAsync(
                surveyId,
                contentType,
                file.FileName,
                incoming.Content.ToArray(),
                options.MaxFileCount,
                cancellationToken)
                .ConfigureAwait(false);
            if (assetId is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.ContentAssetLimitReached, RequestLanguage.Of(context)),
                });
            }

            return Results.Ok(new
            {
                assetId,
                isImage = ContentAsset.IsImage(contentType),
            });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // **編集中の資産を管理画面へ返す。** 公開前の資産は回答画面の口からは出ない
        group.MapGet("/{surveyId:guid}/assets/{assetId:guid}", async (
            Guid surveyId,
            Guid assetId,
            ISurveyAssetStore assets,
            AssetOptions options,
            CancellationToken cancellationToken) =>
        {
            var asset = await assets.FindAsync(surveyId, assetId, cancellationToken)
                .ConfigureAwait(false);

            // **画面内で開かせない。** 管理画面のプレビューでも配布時と同じ扱いにする
            return asset is null
                || !(options.IsAllowed(asset.FileName, asset.ContentType)
                    || HeaderImage.IsAllowedContentType(asset.ContentType))
                ? Results.NotFound()
                : Results.File(
                    asset.Content,
                    asset.ContentType,
                    fileDownloadName: asset.FileName);
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
                draft.Mapping,
                draft.Definition,
                null,
                PleasanterColumn.IsAttachment,
                column => PleasanterColumn.RecordPropertyOf(column)?.ValueKind);
            var historyProblems = draft.AssetHistorySiteId <= 0
                ? []
                : MappingValidator.Validate(
                    draft.AssetHistoryMapping ?? new MappingDefinition(),
                    draft.Definition,
                    null,
                    PleasanterColumn.IsAttachment,
                    column => PleasanterColumn.RecordPropertyOf(column)?.ValueKind,
                    warnUnmappedQuestions: false);
            return Results.Ok(problems.Concat(historyProblems).Select(Describe));
        })
            .AllowAutomation(AdminPermissions.SurveysRead);

        // ---- テスト公開 ------------------------------------------------------
        group.MapPost("/{surveyId:guid}/test-publish", (
            Guid surveyId,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            LookupPublishValidator lookupValidator,
            CancellationToken cancellationToken) =>
            TestPublishAsync(
                surveyId, context, drafts, surveys, lookupValidator, republish: false, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            .AllowAutomation(AdminPermissions.SurveysPublish);

        // ---- テスト公開に反映する --------------------------------------------
        // **下書きへ戻さずに版を固め直す**（Issue #1008）。
        // 以前は画面が「下書きへ戻す → テスト公開」の順に呼んでおり、
        // 割り当ての誤りなどでテスト公開が拒否されると、下書きのまま取り残されていた。
        // **検証を通ってから版を固める。通らなければ、テスト公開中のまま何も変えない。**
        group.MapPost("/{surveyId:guid}/test-republish", (
            Guid surveyId,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            LookupPublishValidator lookupValidator,
            CancellationToken cancellationToken) =>
            TestPublishAsync(
                surveyId, context, drafts, surveys, lookupValidator, republish: true, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            .AllowAutomation(AdminPermissions.SurveysPublish);

        // ---- 本公開 ----------------------------------------------------------
        // **公開は「下書き → 本公開」の 2 ステップ**（Issue #1057）。テスト公開は経由しなくてよい。
        // - **下書き**から押すと、その場で版を固めて（版 +1）本公開する。検査はテスト公開と同じ
        // - **テスト公開中**から押すと、テスト公開で固めた版をそのまま使う
        //   （ここで固め直すと、テストで確かめた版と本番で回答する版が別物になる）。
        //   ただし `reflectDraft = true` のときは、下書きを新しい版へ固めてから本公開する
        //   （テスト公開後に編集した分を入れる。検査はテスト公開と同じ）
        // 本文は省略できる（省略は `reflectDraft = false`。従来の呼び出しは変わらない）
        group.MapPost("/{surveyId:guid}/publish", (
            Guid surveyId,
            PublishRequest? request,
            HttpContext context,
            ISurveyDraftStore drafts,
            ISurveyRepository surveys,
            LookupPublishValidator lookupValidator,
            CancellationToken cancellationToken) =>
            PublishAsync(
                surveyId, request, context, drafts, surveys, lookupValidator, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            .AllowAutomation(AdminPermissions.SurveysPublish);

        // ---- 下書きへ戻す ----------------------------------------------------
        // **テスト公開・本公開・停止中のどれからでも戻せる**（Issue #542）。
        // 版は不変なので、戻しても固めた版は消えない。直して再度テスト公開すると次の版ができる。
        // ⚠️ **戻している間、回答画面は閉じる。** 画面側で確認を挟む
        group.MapPost("/{surveyId:guid}/revert-to-draft", async (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
        {
            var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            if (record.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            if (record.Status is not ((int)SurveyStatus.TestPublished
                or (int)SurveyStatus.Published
                or (int)SurveyStatus.Suspended))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.InvalidSurveyStatus, RequestLanguage.Of(context)),
                });
            }

            // **停止の理由と時刻は消す。** 残すと、下書きに停止の理由が付いて見える
            await surveys.SaveAsync(
                record with
                {
                    Status = (int)SurveyStatus.Draft,
                    SuspendedReason = null,
                    SuspendedAt = null,
                },
                cancellationToken).ConfigureAwait(false);

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "fromStatus", ((SurveyStatus)record.Status).ToString());
            AuditNotes.Add(context, "toStatus", SurveyStatus.Draft.ToString());
            return Results.Ok(new { status = SurveyStatus.Draft.ToString() });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        // ---- Pleasanter サイト ID -------------------------------------------
        group.MapPut("/{surveyId:guid}/site-id", async (
            Guid surveyId,
            UpdateSiteIdRequest request,
            HttpContext context,
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

            var before = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (before?.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            var result = await surveys.UpdatePleasanterSiteIdAsync(
                surveyId, request.PleasanterSiteId, cancellationToken).ConfigureAwait(false);

            if (result == PleasanterSiteUpdateResult.NotFound)
            {
                return Results.NotFound();
            }

            if (result == PleasanterSiteUpdateResult.NotEditable)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SiteIdLockedAfterPublish, RequestLanguage.Of(context)),
                });
            }

            if (result == PleasanterSiteUpdateResult.PendingResponses)
            {
                return Results.Conflict(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SiteIdBlockedByPendingResponses,
                        RequestLanguage.Of(context)),
                });
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            if (before is not null)
            {
                AuditNotes.Add(
                    context,
                    "oldPleasanterSiteId",
                    before.PleasanterSiteId.ToString(CultureInfo.InvariantCulture));
            }
            AuditNotes.Add(
                context,
                "newPleasanterSiteId",
                request.PleasanterSiteId.ToString(CultureInfo.InvariantCulture));

            return Results.Ok(new { pleasanterSiteId = request.PleasanterSiteId });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        // ---- 再編集リンクの一括失効（Issue #202）------------------------------
        // **漏れたと分かったときに、調べずに止められること。**
        // ⚠️ **個別の回答を特定して止めるのは、完全匿名の前提ではどのみちできない**
        // ---- テスト回答の削除（Issue #1010） --------------------------------
        // **Pleasanter 側のレコードを消す、取り返しの付かない操作。** 守りは次のとおり
        // （詳細は `TestResponseCleaner`）。
        // - 対象は本アプリの記録（IsTest = true・Pleasanter へ届いた・送信待ち無し）だけで決め、
        //   **画面から ID を受け取らない**
        // - **画面が見せた件数（expectedCount）と今の件数が違えば、何も消さない**
        // - 消す前に 1 件ずつ取り出し、同じサイト・同じ ID・同じトークンを照合する
        // - **特権管理者とアンケート管理者だけ**（`surveys.deleteTestResponses`。完全削除の
        //   `surveys.delete` とは別。Issue #1024）。操作は監査ログに残す
        group.MapGet("/{surveyId:guid}/test-responses", async (
            Guid surveyId,
            TestResponseCleaner cleaner,
            CancellationToken cancellationToken) =>
        {
            var deletable = await cleaner.CountAsync(surveyId, cancellationToken).ConfigureAwait(false);
            return deletable is null
                ? Results.NotFound()
                : Results.Ok(new { deletable, maxPerRun = TestResponseCleaner.MaxPerRun });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysDeleteTestResponses));

        group.MapPost("/{surveyId:guid}/test-responses/delete", async (
            Guid surveyId,
            // **件数を省略したら断る。** 「全部」の意味にしない
            DeleteTestResponsesRequest? request,
            HttpContext context,
            TestResponseCleaner cleaner,
            CancellationToken cancellationToken) =>
        {
            if (request?.ExpectedCount is not { } expectedCount || expectedCount < 0)
            {
                return Results.BadRequest();
            }

            var result = await cleaner.DeleteAsync(surveyId, expectedCount, cancellationToken)
                .ConfigureAwait(false);

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "surveyId", surveyId.ToString());
            AuditNotes.Add(
                context, "expectedCount", expectedCount.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(context, "result", result.Status.ToString());
            AuditNotes.Add(
                context, "deleted", result.Deleted.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(
                context, "alreadyGone", result.AlreadyGone.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(
                context, "skipped", result.Skipped.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(
                context, "failed", result.Failed.ToString(CultureInfo.InvariantCulture));
            if (!result.ReferenceIds.IsDefaultOrEmpty)
            {
                AuditNotes.Add(
                    context,
                    "referenceIds",
                    string.Join(",", result.ReferenceIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));
            }

            var language = RequestLanguage.Of(context);
            return result.Status switch
            {
                TestResponseCleanupStatus.SurveyNotFound => Results.NotFound(),
                TestResponseCleanupStatus.NotVerifiable => Results.BadRequest(new
                {
                    message = ServerMessages.Get(ServerMessageKeys.TestResponsesNotVerifiable, language),
                }),
                TestResponseCleanupStatus.CountChanged => Results.Conflict(new
                {
                    message = ServerMessages.Get(ServerMessageKeys.TestResponsesCountChanged, language),
                    deletable = result.Current,
                }),
                TestResponseCleanupStatus.Forbidden => Results.Json(
                    new
                    {
                        message = ServerMessages.Get(ServerMessageKeys.TestResponsesForbidden, language),
                        deleted = result.Deleted,
                    },
                    statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Ok(new
                {
                    deleted = result.Deleted,
                    alreadyGone = result.AlreadyGone,
                    skipped = result.Skipped,
                    failed = result.Failed,
                    remaining = result.Remaining,
                }),
            };
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysDeleteTestResponses));

        group.MapPost("/{surveyId:guid}/edit-links/revoke", async (
            Guid surveyId,
            HttpContext context,
            IResponseEditTokenStore editTokens,
            CancellationToken cancellationToken) =>
        {
            var revoked = await editTokens.RevokeBySurveyAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);

            // **何件止めたかを記録に残す**（管理操作なので監査ログの対象）
            AuditNotes.Add(context, "revoked", revoked.ToString(CultureInfo.InvariantCulture));

            return Results.Ok(new { revoked });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        // ---- 公開設定 --------------------------------------------------------
        // **回答数の上限を編集できるようにする**（Issue #53）。
        // 列はあったが、どこからも書けなかった。
        //
        // **proof-of-work の要否もここで切り替える**（Issue #66）。
        // **定義ではなく運用の設定**なので、切り替えても公開し直さなくてよい
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

            // **受付期間は、終了が開始より後でなければ断る**（Issue #1006）。
            // 逆転したまま保存すると、公開しているのに誰も回答できないアンケートができる
            if (request.AcceptPeriod is { From: { } periodFrom, To: { } periodTo }
                && periodTo <= periodFrom)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.AcceptPeriodInvalid, RequestLanguage.Of(context)),
                });
            }

            var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            if (record.ArchivedAt is not null)
            {
                return ArchivedSurvey(context);
            }

            // **受付期間も「指定が無ければ今の値のまま」。** 指定があれば、
            // 空の端は「その側の制限なし」として消す。DB へは UTC で持つ
            var acceptFrom = request.AcceptPeriod is { } fromPeriod
                ? fromPeriod.From?.UtcDateTime
                : record.AcceptFrom;
            var acceptTo = request.AcceptPeriod is { } toPeriod
                ? toPeriod.To?.UtcDateTime
                : record.AcceptTo;

            // **指定が無ければ今の値のまま**（Issue #66）。
            // `bool` で受けて既定値の `false` を書き込むと、
            // **項目を知らない相手が保存しただけで proof-of-work が黙って外れる**
            var requireProofOfWork = request.RequireProofOfWork ?? record.RequireProofOfWork;

            // **同じ理由で下書きも「指定が無ければ今のまま」**（Issue #59）。
            // 既定値の false を書き込むと、項目を知らない相手が保存しただけで
            // 下書きが黙って切れる
            var allowDraft = request.AllowDraft ?? record.AllowDraft;
            var allowEmbedding = request.AllowEmbedding ?? record.AllowEmbedding;

            // **上限を引き上げても勝手に再開しない**（_documents/データモデル設計.md 2.1）。
            // 再開するかどうかは人が決める
            await surveys
                .SaveAsync(
                    record with
                    {
                        ResponseLimit = request.ResponseLimit,
                        RequireProofOfWork = requireProofOfWork,
                        AllowDraft = allowDraft,
                        AllowEmbedding = allowEmbedding,
                        AcceptFrom = acceptFrom,
                        AcceptTo = acceptTo,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(
                new
                {
                    responseLimit = request.ResponseLimit,
                    requireProofOfWork,
                    allowDraft,
                    allowEmbedding,
                    acceptFrom = AsUtc(acceptFrom),
                    acceptTo = AsUtc(acceptTo),
                });
        })
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        // ---- 停止と再開 ------------------------------------------------------
        group.MapPost("/{surveyId:guid}/suspend", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            IResponseTokenStore tokens,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(
                surveyId, SurveyStatus.Suspended, context, surveys, tokens, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            .AllowAutomation(AdminPermissions.SurveysPublish);

        group.MapPost("/{surveyId:guid}/resume", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            IResponseTokenStore tokens,
            CancellationToken cancellationToken) =>
            ChangeStatusAsync(
                surveyId, SurveyStatus.Published, context, surveys, tokens, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish))
            .AllowAutomation(AdminPermissions.SurveysPublish);

        // ---- アーカイブと復元 ------------------------------------------------
        group.MapPost("/{surveyId:guid}/archive", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
            ChangeArchiveAsync(surveyId, archive: true, context, surveys, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        group.MapPost("/{surveyId:guid}/restore", (
            Guid surveyId,
            HttpContext context,
            ISurveyRepository surveys,
            CancellationToken cancellationToken) =>
            ChangeArchiveAsync(surveyId, archive: false, context, surveys, cancellationToken))
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysPublish));

        // ---- 完全削除（Administrator だけ） ----------------------------------
        // ⚠️ **DELETE ではなく POST。** 題名の確認を本文で受け取る必要があるが、
        // **最小 API は DELETE の本文を推論で受け取らない**（起動時に落ちる）。
        // 中継するものが DELETE の本文を落とすこともある。
        // この節の状態変更（archive / restore / test-publish）と形もそろう
        group.MapPost("/{surveyId:guid}/delete", async (
            Guid surveyId,
            // **本文を省略しても落ちないようにする。** 題名の不一致として断り、**消えない方へ倒す**
            DeleteSurveyRequest? request,
            HttpContext context,
            ISurveyDeletionStore deletion,
            CancellationToken cancellationToken) =>
        {
            var result = await deletion.DeleteAsync(
                surveyId, request?.Title, cancellationToken).ConfigureAwait(false);

            if (result.Status == SurveyDeletionStatus.NotFound)
            {
                return Results.NotFound();
            }

            if (result.Status == SurveyDeletionStatus.Template)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
                });
            }

            if (result.Status == SurveyDeletionStatus.NotArchived)
            {
                return Results.Conflict(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyDeleteRequiresArchive, RequestLanguage.Of(context)),
                });
            }

            if (result.Status == SurveyDeletionStatus.TitleMismatch)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyDeleteTitleMismatch, RequestLanguage.Of(context)),
                });
            }

            if (result.Status == SurveyDeletionStatus.PendingDelivery)
            {
                return Results.Conflict(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SurveyDeleteBlockedByPendingDelivery,
                        RequestLanguage.Of(context)),
                });
            }

            var deleted = result.Survey
                ?? throw new InvalidOperationException("削除結果にアンケート情報がありません。");
            AuditNotes.SetTarget(context, "survey", deleted.SurveyId.ToString());
            AuditNotes.Add(context, "surveyId", deleted.SurveyId.ToString());
            AuditNotes.Add(context, "publicId", deleted.PublicId);
            AuditNotes.Add(context, "title", deleted.Title);
            AuditNotes.Add(
                context,
                "pleasanterSiteId",
                deleted.PleasanterSiteId.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(
                context,
                "responseCount",
                deleted.ResponseCount.ToString(CultureInfo.InvariantCulture));
            AuditNotes.Add(
                context,
                "archivedAt",
                deleted.ArchivedAt.ToString("O", CultureInfo.InvariantCulture));

            return Results.NoContent();
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysDelete));

        return builder;
    }

    /// <summary>
    /// 下書きの保存の本体。テスト公開中なら、保存と同時に版を固め直す（Issue #1057）。
    /// <paramref name="request"/> の定義とマッピングは <c>null</c> でないこと（呼び出し側が確かめる）。
    /// </summary>
    internal static async Task<IResult> SaveDraftCoreAsync(
        Guid surveyId,
        SaveDraftRequest request,
        SurveyRecord record,
        HttpContext context,
        ISurveyDraftStore drafts,
        ISurveyRepository surveys,
        LookupPublishValidator lookupValidator,
        CancellationToken cancellationToken)
    {
        var requestDefinition = request.Definition!;
        var requestMapping = request.Mapping!;
        // **テスト公開中の保存は、自動で版へ反映する**（Issue #1057）。
        // 保存のたびに版を固め直す（+1）。URL で見える内容＝最後に保存した内容。
        // 反映できるのは、公開の権限を持つ呼び出し（キーならスコープ）だけ。
        // **保存と版固めは 1 つの操作として扱う。** 検査を、保存の**前に**候補の内容へ掛け、
        // 通らなければ**保存もしない**（下書きと版が食い違わない）。
        // ⚠️ 不備のある編集を保存したいときは、テスト公開を OFF（下書きへ戻す）にする
        var reflect = record.Status == (int)SurveyStatus.TestPublished
            && !record.IsTemplate
            && CanPublish(context);
        var warnings = new List<MappingProblem>();
        SurveyDraft? candidate = null;
        if (reflect)
        {
            candidate = new SurveyDraft(
                requestDefinition with { Version = (record.PublishedVersion ?? 0) + 1 },
                requestMapping,
                request.Revision,
                request.AssetHistorySiteId,
                request.AssetHistoryMapping);
            var rejected = await ValidateForFreezeAsync(
                surveyId, candidate, record, context, lookupValidator, warnings, cancellationToken)
                .ConfigureAwait(false);
            if (rejected is not null)
            {
                return rejected;
            }
        }

        // **それ以外は、不備があっても保存はさせる。** 直している途中で保存できないと作業にならない。
        // **拒否するのは公開のとき**
        try
        {
            var revision = await drafts.SaveAsync(
                surveyId,
                requestDefinition,
                requestMapping,
                request.Revision,
                cancellationToken,
                request.AssetHistorySiteId,
                request.AssetHistoryMapping)
                .ConfigureAwait(false);

            if (candidate is null)
            {
                return Results.Ok(new { revision });
            }

            var committed = await CommitFreezeAsync(
                surveyId,
                candidate with { Revision = revision },
                context,
                surveys,
                SurveyStatus.TestPublished,
                SurveyStatus.TestPublished,
                warnings,
                cancellationToken).ConfigureAwait(false);

            // **保存はできたが、版を固める段で失敗した**（同時の公開など）。
            // 保存済みの版（revision）を返し、反映できなかったことを伝える。
            // 状態はテスト公開のまま。画面は「下書きの変更が版に未反映」と出し、次の保存でやり直せる
            return committed is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }
                ? Results.Ok(new
                {
                    revision,
                    reflected = true,
                    version = candidate.Definition.Version,
                    warnings = warnings.Select(Describe).ToList(),
                })
                : Results.Ok(new
                {
                    revision,
                    reflected = false,
                    reflectError = ServerMessages.Get(
                        ServerMessageKeys.VersionAlreadyPublished, RequestLanguage.Of(context)),
                });
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
    }

    /// <summary>
    /// 呼び出しが公開の権限を持つか。**キー（Bearer）なら付けたスコープ、画面なら役割**で見る。
    /// </summary>
    private static bool CanPublish(HttpContext context) =>
        AdminApiTokenAuthentication.IsApiToken(context.User)
            ? AdminApiTokenAuthentication.PermissionsOf(context.User).Contains(AdminPermissions.SurveysPublish)
            : AdminPermissions.Has(context.User.FindFirstValue(ClaimTypes.Role), AdminPermissions.SurveysPublish);

    /// <summary>本公開（Issue #1057）。下書き・テスト公開中のどちらからも押せる。</summary>
    internal static async Task<IResult> PublishAsync(
        Guid surveyId,
        PublishRequest? request,
        HttpContext context,
        ISurveyDraftStore drafts,
        ISurveyRepository surveys,
        LookupPublishValidator lookupValidator,
        CancellationToken cancellationToken)
    {
        var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Results.NotFound();
        }

        if (record.ArchivedAt is not null)
        {
            return ArchivedSurvey(context);
        }

        // 下書きからは常にその場で固める。テスト公開中は、反映を求められたときだけ固め直す
        if (record.Status == (int)SurveyStatus.Draft
            || (record.Status == (int)SurveyStatus.TestPublished
                && request?.ReflectDraft == true))
        {
            return await FreezeAndPublishAsync(
                surveyId,
                context,
                drafts,
                surveys,
                lookupValidator,
                (SurveyStatus)record.Status,
                SurveyStatus.Published,
                cancellationToken).ConfigureAwait(false);
        }

        if (record.Status != (int)SurveyStatus.TestPublished
            || record.PublishedVersion is null)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.InvalidSurveyStatus, RequestLanguage.Of(context)),
            });
        }

        await surveys.SaveAsync(
            record with { Status = (int)SurveyStatus.Published },
            cancellationToken).ConfigureAwait(false);

        AuditNotes.SetTarget(context, "survey", surveyId.ToString());
        AuditNotes.Add(context, "fromStatus", SurveyStatus.TestPublished.ToString());
        AuditNotes.Add(context, "toStatus", SurveyStatus.Published.ToString());
        return Results.Ok(new
        {
            status = SurveyStatus.Published.ToString(),
            version = record.PublishedVersion,
        });
    }

    /// <summary>テスト公開、またはテスト公開中の版の固め直し。</summary>
    /// <param name="republish">
    /// <c>true</c> ならテスト公開中のアンケートの固め直し（Issue #1008）。
    /// **状態は動かさない。** 検証に落ちても、版を固める段で失敗しても、反映前のテスト公開のまま残る。
    /// </param>
    internal static async Task<IResult> TestPublishAsync(
        Guid surveyId,
        HttpContext context,
        ISurveyDraftStore drafts,
        ISurveyRepository surveys,
        LookupPublishValidator lookupValidator,
        bool republish,
        CancellationToken cancellationToken) =>
        await FreezeAndPublishAsync(
            surveyId,
            context,
            drafts,
            surveys,
            lookupValidator,
            republish ? SurveyStatus.TestPublished : SurveyStatus.Draft,
            SurveyStatus.TestPublished,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 下書きを検査して版を固め、<paramref name="target"/> の状態にする（Issue #1057）。
    /// テスト公開・テスト公開の固め直し・下書きからの本公開・テスト公開からの「下書きを反映して本公開」が共有する。
    /// **検査は 1 か所**（どの入口でも同じ検査を通す）。
    /// </summary>
    /// <param name="from">この状態のときだけ受け付ける。</param>
    /// <param name="target">固めた後の状態（<c>TestPublished</c> か <c>Published</c>）。</param>
    private static async Task<IResult> FreezeAndPublishAsync(
        Guid surveyId,
        HttpContext context,
        ISurveyDraftStore drafts,
        ISurveyRepository surveys,
        LookupPublishValidator lookupValidator,
        SurveyStatus from,
        SurveyStatus target,
        CancellationToken cancellationToken)
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

        if (beforePublish is null)
        {
            return Results.NotFound();
        }

        if (beforePublish.ArchivedAt is not null)
        {
            return ArchivedSurvey(context);
        }

        if (beforePublish.Status != (int)from)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.InvalidSurveyStatus, RequestLanguage.Of(context)),
            });
        }

        var warnings = new List<MappingProblem>();
        var rejected = await ValidateForFreezeAsync(
            surveyId, draft, beforePublish, context, lookupValidator, warnings, cancellationToken)
            .ConfigureAwait(false);
        if (rejected is not null)
        {
            return rejected;
        }

        return await CommitFreezeAsync(
            surveyId, draft, context, surveys, from, target, warnings, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 版を固める前の検査（Issue #1057）。**テスト公開・本公開・テスト公開中の保存の反映が共有する 1 か所。**
    /// 通らなければ断る応答を返す（<c>null</c> なら通った）。通ったときの警告は <paramref name="warnings"/> へ入れる。
    /// </summary>
    private static async Task<IResult?> ValidateForFreezeAsync(
        Guid surveyId,
        SurveyDraft draft,
        SurveyRecord beforePublish,
        HttpContext context,
        LookupPublishValidator lookupValidator,
        List<MappingProblem> warnings,
        CancellationToken cancellationToken)
    {
        // **公開のときだけ拒否する。** 壊れた定義で回答を受け付けると、
        // 受け付けた回答が Pleasanter へ届かないまま溜まる
        // **取り込み（Issue #1009）と同じ検査を通す。** 定義だけで決まる検査は
        // `PublishInspector` に 1 つだけ置き、片方に検査を足し漏らさない
        var inspection = PublishInspector.Inspect(draft.Definition, draft.Mapping);
        var problems = inspection.MappingProblems;
        var blocking = problems.Where(problem => problem.IsBlocking).ToList();
        if (draft.AssetHistorySiteId > 0)
        {
            if (draft.AssetHistorySiteId == beforePublish.PleasanterSiteId)
            {
                blocking.Add(new MappingProblem(
                    MappingProblemCode.InvalidShape,
                    Detail: "assetHistorySiteIdMustDiffer"));
            }

            if (draft.AssetHistoryMapping is null
                || draft.AssetHistoryMapping.Assignments.IsDefaultOrEmpty)
            {
                blocking.Add(new MappingProblem(
                    MappingProblemCode.InvalidShape,
                    Detail: "assetHistory"));
            }

            blocking.AddRange(MappingValidator.Validate(
                draft.AssetHistoryMapping ?? new MappingDefinition(),
                draft.Definition,
                null,
                PleasanterColumn.IsAttachment,
                column => PleasanterColumn.RecordPropertyOf(column)?.ValueKind,
                warnUnmappedQuestions: false).Where(problem => problem.IsBlocking));
        }
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
        var flowProblems = inspection.Flow;
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

        // **送れない自動返信のまま公開しない**（Issue #189）。
        // 有効にしたのに宛先が無い・件名が空だと、**送ったつもりで 1 通も出ない。**
        // 気付くのは回答者から問い合わせが来たときになる
        var autoReplyProblems = inspection.AutoReply;
        if (autoReplyProblems.Length > 0)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.PublishBlockedByAutoReply, RequestLanguage.Of(context)),
                autoReply = autoReplyProblems.Select(problem => new
                {
                    code = problem.Code.ToString(),
                    detail = problem.Detail,
                }),
            });
        }

        if (inspection.AssetTicketDaysInvalid)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.PublishBlockedBySettings, RequestLanguage.Of(context)),
                settings = new[]
                {
                    new
                    {
                        code = "AssetTicketDaysInvalid",
                        questionId = (string?)null,
                        detail = (draft.Definition.AssetDelivery?.Days ?? 0).ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    },
                },
            });
        }

        // **答えようのない設問のまま公開しない**（Issue #101）。
        // 「5 つの選択肢から 7 つ選べ」は回答者が何をしても通らない
        var settingsProblems = inspection.Settings;
        if (settingsProblems.Length > 0)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.PublishBlockedBySettings, RequestLanguage.Of(context)),
                settings = settingsProblems.Select(problem => new
                {
                    code = problem.Code.ToString(),
                    questionId = problem.QuestionId,
                    detail = problem.Detail,
                }),
            });
        }

        // **不備のあるルックアップのまま公開しない**（Issue #879・仕様書 3.2・8 章）。
        // 構造・資格情報・参照元の列の型・統合/継承を見る。参照元の列定義を取得できなければ公開させない。
        // ルックアップが無いアンケートでは外部を呼ばない
        var lookupProblems = await lookupValidator
            .ValidateAsync(surveyId, draft.Definition, cancellationToken).ConfigureAwait(false);
        if (lookupProblems.Count > 0)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.PublishBlockedByLookup, RequestLanguage.Of(context)),
                lookups = lookupProblems.Select(problem => new
                {
                    lookupId = problem.LookupId,
                    kind = problem.Kind,
                    code = problem.Code,
                    detail = problem.Detail,
                }),
            });
        }

        if (inspection.NoAnswerableQuestion)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.NoAnswerableQuestion, RequestLanguage.Of(context)),
            });
        }

        warnings.AddRange(problems.Where(problem => !problem.IsBlocking));
        return null;
    }

    /// <summary>
    /// 検査を通った下書きを版として固め、<paramref name="target"/> の状態にする（Issue #1057）。
    /// 版を固める段で衝突したら、状態は変えずに 409 を返す。
    /// </summary>
    private static async Task<IResult> CommitFreezeAsync(
        Guid surveyId,
        SurveyDraft draft,
        HttpContext context,
        ISurveyRepository surveys,
        SurveyStatus from,
        SurveyStatus target,
        List<MappingProblem> warnings,
        CancellationToken cancellationToken)
    {
        var publishedBy = context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id
            && Guid.TryParse(id, out var adminUserId)
            ? adminUserId
            : (Guid?)null;

        try
        {
            await surveys.PublishWithAssetHistoryAsync(
                surveyId,
                draft.Definition.Version,
                draft.Definition,
                draft.Mapping,
                publishedBy,
                draft.AssetHistorySiteId,
                draft.AssetHistoryMapping,
                draft.Revision,
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
                record with { Status = (int)target }, cancellationToken)
                .ConfigureAwait(false);
        }

        AuditNotes.SetTarget(context, "survey", surveyId.ToString());
        AuditNotes.Add(context, "fromStatus", from.ToString());
        AuditNotes.Add(context, "toStatus", target.ToString());

        var warningBody = warnings.Select(Describe).ToList();

        // 本公開のときだけ状態を返す（テスト公開の応答の形は変えない）
        return target == SurveyStatus.Published
            ? Results.Ok(new
            {
                status = target.ToString(),
                version = draft.Definition.Version,
                warnings = warningBody,
            })
            : Results.Ok(new
            {
                version = draft.Definition.Version,
                warnings = warningBody,
            });
    }

    private static IResult ContentAssetRejected(
        HttpContext context,
        IEnumerable<string>? reasons = null) =>
        Results.BadRequest(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.ContentAssetRejected, RequestLanguage.Of(context)),
            reasons,
        });

    private static async Task<IResult> ChangeArchiveAsync(
        Guid surveyId,
        bool archive,
        HttpContext context,
        ISurveyRepository surveys,
        CancellationToken cancellationToken)
    {
        var record = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Results.NotFound();
        }

        // **テンプレートは別の一覧と削除の仕組みを持つ。**
        // アーカイブを混ぜると、テンプレート一覧から戻す口が無くなる。
        if (record.IsTemplate)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
            });
        }

        if (archive == (record.ArchivedAt is not null))
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.InvalidArchiveState, RequestLanguage.Of(context)),
            });
        }

        var archivedAt = archive ? DbTime.UtcNowTruncated() : (DateTime?)null;
        await surveys.SaveAsync(record with { ArchivedAt = archivedAt }, cancellationToken)
            .ConfigureAwait(false);

        AuditNotes.SetTarget(context, "survey", surveyId.ToString());
        AuditNotes.Add(context, "archive", archive ? "true" : "false");

        return Results.Ok(new { archivedAt });
    }

    private static IResult ImportInvalid(
        HttpContext context,
        SurveyExchangeError error,
        string? detail = null) =>
        Results.BadRequest(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.SurveyImportInvalid, RequestLanguage.Of(context)),
            code = error.ToString(),
            detail,
        });

    private static IResult ArchivedSurvey(HttpContext context) =>
        Results.Conflict(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.SurveyArchived, RequestLanguage.Of(context)),
        });

    private static IResult PleasanterFailure(PleasanterResponse response, HttpContext context) =>
        Results.Json(
            new
            {
                message = string.IsNullOrWhiteSpace(response.Message)
                    ? ServerMessages.Get(
                        ServerMessageKeys.PleasanterSiteWriteFailed, RequestLanguage.Of(context))
                    : response.Message,
            },
            statusCode: response.DisplayStatusCode() ?? StatusCodes.Status502BadGateway);

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

        if (record.ArchivedAt is not null)
        {
            return ArchivedSurvey(context);
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

        if ((status == SurveyStatus.Suspended && record.Status != (int)SurveyStatus.Published)
            || (status == SurveyStatus.Published && record.Status != (int)SurveyStatus.Suspended))
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.InvalidSurveyStatus, RequestLanguage.Of(context)),
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

        AuditNotes.SetTarget(context, "survey", surveyId.ToString());
        AuditNotes.Add(context, "fromStatus", ((SurveyStatus)record.Status).ToString());
        AuditNotes.Add(context, "toStatus", status.ToString());

        return Results.Ok(new { status = status.ToString() });
    }

    /// <summary>一覧を応答の形にする（Issue #79）。</summary>
    /// <param name="rows">1 件多く読んだ行。</param>
    /// <param name="take">実際に返す件数。</param>
    public static SurveyPageResponse ToResponse(IReadOnlyList<SurveySummary> rows, int take)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new SurveyPageResponse([.. rows.Take(take)], rows.Count > take);
    }

    /// <summary>
    /// <c>GetSite</c> 応答から、列の物理名ごとの設定（項目名・リンク・選択肢）を取り出す（Issue #549）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **編集画面の項目名（<c>LabelText</c>）と、一覧の項目名（<c>GridLabelText</c>）は別。**
    /// 実機（Pleasanter 1.5.8.1）で、両方が別々に返ることを確かめた。両方を返す。
    /// </para>
    /// <para>
    /// **選択肢は <c>ChoicesText</c>**（1 行 1 件で <c>値,表示文字列</c>）。
    /// **リンクの指定は 2 通り**（実機 Pleasanter 1.5.8.1 で確認）。
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// 行の形: <c>[[サイト ID]]</c>（<c>[[1,NoAddButton]]</c> のように options を続けられる）。
    /// 選択肢と混在できる。<c>Link: true</c> が付く。
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// JSON の形: <c>ChoicesText</c> 全体が <c>[{"SiteId":1,...},{"SiteId":2}]</c> のリンク定義の配列。
    /// <b><c>Link: true</c> は付かない</b>ので、<c>Link</c> だけでは見分けられない。
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// 参照先のレコードは列挙できないので、参照先（サイト ID など）だけを返す。
    /// </para>
    /// <para>
    /// **名前を付けていない列は応答に載らない**（実機）。設定を初期化した列も載らなくなる。
    /// 空のもの、物理名と同じ項目名は返さない（本アプリの同期が書く値で、情報が増えない）。
    /// 物理名の大文字小文字は区別しない。
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, ColumnDetail> ColumnDetailsFrom(JsonNode? body)
    {
        var details = new Dictionary<string, ColumnDetail>(StringComparer.OrdinalIgnoreCase);
        var columns = body?["Response"]?["Data"]?["SiteSettings"]?["Columns"]?.AsArray();
        if (columns is null)
        {
            return details;
        }

        foreach (var column in columns)
        {
            var columnName = column?["ColumnName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            var label = UserLabel(columnName, column?["LabelText"]?.GetValue<string>());
            var gridLabel = UserLabel(columnName, column?["GridLabelText"]?.GetValue<string>());
            var choicesText = column?["ChoicesText"]?.GetValue<string>();
            var jsonReferences = ParseJsonLinks(choicesText);
            var (choices, references, choiceCount) = jsonReferences is null
                ? ParseChoices(choicesText)
                : ((IReadOnlyList<ColumnChoice>)[], jsonReferences, 0);
            // **JSON の形は `Link: true` が付かない**ので、参照先があることからもリンクと判断する
            var isLink = (column?["Link"]?.GetValue<bool>() ?? false) || references.Count > 0;
            var linkFormat = !isLink ? null : jsonReferences is null ? "Lines" : "Json";
            var choicesControlType = column?["ChoicesControlType"]?.GetValue<string>();
            if (label is null && gridLabel is null && !isLink && choiceCount == 0)
            {
                continue;
            }

            details[columnName] = new ColumnDetail(
                label,
                gridLabel,
                isLink,
                linkFormat,
                choices,
                choiceCount,
                references,
                choiceCount > 0 && !string.IsNullOrWhiteSpace(choicesControlType)
                    ? choicesControlType
                    : null);
        }

        return details;
    }

    /// <summary>
    /// <c>ChoicesText</c> が JSON のリンク定義（<c>[{"SiteId":1,...}]</c>）なら、参照先のサイト ID を返す。
    /// そうでなければ <c>null</c>。**サイト ID を持つ要素が 1 つも無ければ「JSON のリンク」とは見なさない。**
    /// </summary>
    private static IReadOnlyList<string>? ParseJsonLinks(string? choicesText)
    {
        var text = choicesText?.Trim();
        // **`[[1]]` の行の形と取り違えない。** こちらは先頭が `[[` で、JSON の配列として読めない
        if (string.IsNullOrEmpty(text) || text[0] != '[' || text.StartsWith("[[", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonArray array)
            {
                return null;
            }

            var siteIds = array
                .Select(item => item?["SiteId"]?.GetValue<long>())
                .Where(siteId => siteId is > 0)
                .Select(siteId => siteId!.Value.ToString(CultureInfo.InvariantCulture))
                // **同じサイトを複数書けるので、重複は除く**（「参照先: 1, 1」と出さない。Issue #558）
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return siteIds.Count == 0 ? null : siteIds;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            // **読めない形は、リンクではなく選択肢の行として扱う。** 画面を止めない
            return null;
        }
    }

    /// <summary>画面へ返す選択肢の上限。**全部返すと応答が膨らむ**（件数は別に返す）。</summary>
    private const int MaxReturnedChoices = 50;

    /// <summary>
    /// <c>ChoicesText</c> を選択肢と参照先へ分ける。**1 行 1 件。**
    /// <c>[[…]]</c> の行は参照先（リンク・利用者・部署など）で、選択肢には数えない。
    /// <c>値,表示文字列</c> の形で、表示文字列が無ければ値をそのまま表示に使う。
    /// </summary>
    private static (IReadOnlyList<ColumnChoice> Choices, IReadOnlyList<string> References, int Count)
        ParseChoices(string? choicesText)
    {
        var choices = new List<ColumnChoice>();
        var references = new List<string>();
        var count = 0;
        foreach (var raw in (choicesText ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal)
                && line.EndsWith("]]", StringComparison.Ordinal))
            {
                references.Add(line[2..^2]);
                continue;
            }

            count++;
            if (choices.Count >= MaxReturnedChoices)
            {
                continue;
            }

            var comma = line.IndexOf(',', StringComparison.Ordinal);
            choices.Add(comma < 0
                ? new ColumnChoice(line, line)
                : new ColumnChoice(line[..comma].Trim(), line[(comma + 1)..].Trim()));
        }

        return (choices, references, count);
    }

    /// <summary>利用者が付けた名前だけを返す。空と、物理名と同じものは「名前なし」。</summary>
    private static string? UserLabel(string columnName, string? text) =>
        string.IsNullOrWhiteSpace(text)
        || string.Equals(columnName, text, StringComparison.OrdinalIgnoreCase)
            ? null
            : text;

    /// <summary><c>GetSite</c> 応答から、接頭辞ごとの使える列の本数を見積もる（Issue #551）。</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ **<c>Columns</c> の件数は使える本数ではない。** 実機（Pleasanter 1.5.8.1）で、
    /// <c>GetSite</c> は**設定を付けた列だけ**を返し、付けていない列も、設定を初期化した列も載らない
    /// ことを確かめた（<c>_documents/実機検証結果.md</c> 11 章）。載っている数を本数にすると、
    /// 名前を付けた列が数本あるだけで「使える本数はその数本」と見なされ、足りないと誤判定する。
    /// </para>
    /// <para>
    /// **見積もりは「標準の 26 本」を下限にする。** 項目拡張の列（<c>Class001</c> など）は、
    /// 応答に載っていた最大の番号を上乗せする。**実際の本数の下限**であり、
    /// 応答からは項目拡張の本数そのものは分からない。**足りないと誤って言わない側に倒す。**
    /// </para>
    /// <para>
    /// 認識できる列が 1 つも載っていなければ <c>null</c>（標準の本数として扱う）。
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, int>? AvailableColumnsFrom(JsonNode? body)
    {
        var columns = body?["Response"]?["Data"]?["SiteSettings"]?["Columns"]?.AsArray();
        if (columns is null || columns.Count == 0)
        {
            return null;
        }

        var extensionMaxByPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var columnName = column?["ColumnName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(columnName) || PleasanterColumn.KindOf(columnName) is null)
            {
                continue;
            }

            var prefix = ColumnBudget.PrefixOf(columnName);
            // 英字 1 文字（ClassA）は標準の 26 本の中。数字（Class012）は項目拡張で、番号が本数の下限になる
            var extensionIndex = int.TryParse(
                columnName.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index)
                    ? index
                    : 0;
            extensionMaxByPrefix[prefix] = Math.Max(
                extensionMaxByPrefix.GetValueOrDefault(prefix), extensionIndex);
        }

        return extensionMaxByPrefix.Count == 0
            ? null
            : extensionMaxByPrefix.ToDictionary(
                pair => pair.Key,
                pair => ColumnBudget.StandardColumnsPerType + pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>GetSite</c> は成功したが、サイトの設定（<c>SiteSettings</c>）が返らなかったか。
    /// Pleasanter 1.5.10.0 以降は、サイトを管理できない API キーの利用者に返さない（Issue #995）。
    /// **この状態で <c>UpdateSite</c> へ進まない**（設定を丸ごと置き換える API なので、読めないまま書かない）。
    /// </summary>
    internal static bool SiteSettingsHidden(JsonNode? body) =>
        body?["Response"]?["Data"] is JsonObject && body["Response"]!["Data"]!["SiteSettings"] is null;

    private static IResult SiteSettingsNotReadable(HttpContext context) =>
        Results.BadRequest(new
        {
            code = "SiteSettingsNotReadable",
            message = ServerMessages.Get(ServerMessageKeys.SiteSettingsNotReadable, RequestLanguage.Of(context)),
        });

    /// <summary>不備を画面に出せる形にする。</summary>
    private static object Describe(MappingProblem problem) => new
    {
        code = problem.Code.ToString(),
        targetColumn = problem.TargetColumn,
        detail = problem.Detail,
        isBlocking = problem.IsBlocking,
    };

    private static bool CanImportQuestions(
        SurveyRecord? target,
        SurveyRecord? source,
        Guid targetSurveyId,
        Guid sourceSurveyId) =>
        target is { ArchivedAt: null, IsTemplate: false }
        && source is { ArchivedAt: null }
        && targetSurveyId != sourceSurveyId;

    private static IResult QuestionImportSourceInvalid(HttpContext context) =>
        Results.BadRequest(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.QuestionImportSourceInvalid,
                RequestLanguage.Of(context)),
        });

    private static IResult QuestionImportSelectionRequired(HttpContext context) =>
        Results.BadRequest(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.QuestionImportSelectionRequired,
                RequestLanguage.Of(context)),
        });

    /// <summary>一覧の 1 ページ（Issue #79）。</summary>
    /// <param name="Items">このページに出すアンケート。</param>
    /// <param name="HasMore">
    /// 次のページがあるか。**総数は数えていない。**
    /// 1 件多く読んで、余ったかどうかだけを見ている。
    /// </param>
    public sealed record SurveyPageResponse(
        IReadOnlyList<SurveySummary> Items,
        bool HasMore);

    /// <summary>マッピング編集画面で使う列数。</summary>
    public sealed record ColumnAvailabilityResponse(
        string Source,
        IReadOnlyDictionary<string, int> AvailableByPrefix,
        IReadOnlyDictionary<string, ColumnDetail> Columns);

    /// <summary>列の設定。編集画面と一覧で別の項目名を付けられるので両方持つ。</summary>
    /// <param name="Label">編集画面の項目名（<c>LabelText</c>）。</param>
    /// <param name="GridLabel">一覧の項目名（<c>GridLabelText</c>）。</param>
    /// <param name="IsLink">リンク項目か（<c>Link: true</c>、または JSON のリンク定義がある）。</param>
    /// <param name="LinkFormat">リンクの指定の形。<c>Lines</c>（<c>[[サイト ID]]</c> の行）か <c>Json</c>。リンクでなければ <c>null</c>。</param>
    /// <param name="Choices">選択肢（上限あり。全件の数は <paramref name="ChoiceCount"/>）。</param>
    /// <param name="ChoiceCount">選択肢の全件数。</param>
    /// <param name="References">参照先（<c>[[…]]</c> の中身、または JSON の <c>SiteId</c>。サイト ID・Users・Depts など）。</param>
    /// <param name="ChoicesControlType">選択肢の見せ方（<c>Radio</c> など。選択肢があるときだけ）。</param>
    public sealed record ColumnDetail(
        string? Label,
        string? GridLabel,
        bool IsLink,
        string? LinkFormat,
        IReadOnlyList<ColumnChoice> Choices,
        int ChoiceCount,
        IReadOnlyList<string> References,
        string? ChoicesControlType);

    /// <summary>選択肢 1 件。<c>値,表示文字列</c>。</summary>
    public sealed record ColumnChoice(string Value, string Text);

    /// <summary>取り込み元のページ。**ページそのものは取り込まない。**</summary>
    public sealed record QuestionImportPageResponse(
        string PageId,
        LocalizedText? Title,
        IReadOnlyList<QuestionImportQuestionResponse> Questions);

    /// <summary>取り込み候補として見せる設問。</summary>
    public sealed record QuestionImportQuestionResponse(
        string QuestionId,
        QuestionType Type,
        LocalizedText Title);

    /// <summary>取り込み元から選べる設問。</summary>
    public sealed record QuestionImportSourceResponse(
        IReadOnlyList<QuestionImportPageResponse> Pages);

    /// <summary>選んだ設問を取り込む。</summary>
    /// <param name="QuestionIds">取り込み元で選んだ設問 ID。</param>
    /// <param name="ExistingQuestionIds">
    /// 取り込み先の画面にある設問 ID。**未保存の設問も含めて衝突を避ける。**
    /// </param>
    public sealed record QuestionImportRequest(
        IReadOnlyList<string>? QuestionIds,
        IReadOnlyList<string>? ExistingQuestionIds);

    /// <summary>アンケートを新しく作る。</summary>
    public sealed record CreateSurveyRequest(
        string? Title,
        long PleasanterSiteId,
        string? ResponseJsonColumn,
        bool CreatePleasanterSite = false,
        long ParentPleasanterSiteId = 0);

    /// <summary>アンケートを複製する（Issue #46）。</summary>
    /// <param name="PleasanterSiteId">
    /// **複製先が書き込むサイト。** 元の値をコピーしないので、必ず指定させる。
    /// </param>
    /// <param name="ResponseJsonColumn">
    /// 回答 JSON の正本を入れる列。**サイトに紐づく値なのでコピーしない。**
    /// </param>
    public sealed record DuplicateSurveyRequest(
        long PleasanterSiteId,
        string? ResponseJsonColumn);

    /// <summary>公開設定の保存（Issue #53）。</summary>
    /// <param name="ResponseLimit">
    /// 受け付ける回答の上限。**<c>null</c> は「上限なし」。**
    /// </param>
    /// <param name="RequireProofOfWork">
    /// 回答の送信に proof-of-work を課すか（Issue #66）。
    /// **<c>null</c> は「変えない」。** <c>false</c> と取り違えないこと。
    ///
    /// **<c>bool</c> で受けないのは、省略されたときに <c>false</c> になるため。**
    /// 項目を知らない相手が公開設定を保存しただけで、守りが黙って外れてしまう。
    /// </param>
    /// <param name="AllowDraft">
    /// 回答の下書きを端末へ残すか（Issue #59）。**<c>null</c> は「変えない」。**
    ///
    /// ⚠️ **入れると、回答の途中が端末に残る。** 共有の端末では、
    /// 次に使う人が前の人の回答を見ることになる。**既定は無効。**
    /// </param>
    /// <param name="AllowEmbedding">
    /// 回答画面を運用側が許可した親サイトへ埋め込んでよいか（Issue #334）。
    /// **<c>null</c> は「変えない」。既定は無効。**
    /// </param>
    /// <param name="AcceptPeriod">
    /// 受付期間（Issue #1006）。**<c>null</c>（項目ごと省略）は「変えない」。**
    /// 項目があれば、<c>From</c>・<c>To</c> の空は「その側の制限なし」として保存する。
    /// 期間外の回答は、理由を出さずに受け付けない（受付を止めたときと同じ扱い）。
    /// </param>
    public sealed record SurveySettingsRequest(
        int? ResponseLimit,
        bool? RequireProofOfWork = null,
        bool? AllowDraft = null,
        bool? AllowEmbedding = null,
        AcceptPeriodRequest? AcceptPeriod = null);

    /// <summary>受付期間。時刻は UTC かオフセット付きで受け取り、UTC に直して保存する。</summary>
    public sealed record AcceptPeriodRequest(DateTimeOffset? From, DateTimeOffset? To);

    private static DateTime? AsUtc(DateTime? value) =>
        value is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    /// <summary>テスト公開まで変更できる Pleasanter サイト ID。</summary>
    public sealed record UpdateSiteIdRequest(long PleasanterSiteId);

    /// <summary>本公開の要求（Issue #1057）。</summary>
    /// <param name="ReflectDraft">
    /// テスト公開中のとき、下書きを新しい版へ固めてから本公開するか。省略は <c>false</c>（固めた版のまま）。
    /// 下書きからの本公開では常に固めるので、この値は効かない。
    /// </param>
    public sealed record PublishRequest(bool ReflectDraft = false);

    /// <summary>完全削除の確認。**現在の題名と完全に一致しなければ削除しない。**</summary>
    public sealed record DeleteSurveyRequest(string? Title);

    /// <summary>テスト回答の削除の確認（Issue #1010）。</summary>
    /// <param name="ExpectedCount">画面が見せた、消せるテスト回答の件数。**今の件数と違えば何も消さない。**</param>
    public sealed record DeleteTestResponsesRequest(int? ExpectedCount);

    /// <summary>下書きの保存。</summary>
    /// <param name="Revision">読んだときの版。**これが今の版と違えば拒否する。**</param>
    public sealed record SaveDraftRequest(
        SurveyDefinition? Definition,
        MappingDefinition? Mapping,
        int Revision,
        long AssetHistorySiteId = 0,
        MappingDefinition? AssetHistoryMapping = null);

    /// <summary>取り込みの要求（Issue #1009）。</summary>
    /// <param name="Document">書き出した JSON（<c>SurveyExchange</c> の入れ物）。</param>
    /// <param name="PleasanterSiteId">書き込み先のサイト。**入れ物には入っていないので指定させる。**</param>
    /// <param name="ResponseJsonColumn">回答 JSON の正本を入れる列。**サイトに紐づく値なので入れ物に無い。**</param>
    /// <param name="DryRun"><c>true</c> なら検査だけで、何も作らない。</param>
    public sealed record ImportSurveyRequest(
        System.Text.Json.JsonElement Document,
        long PleasanterSiteId,
        string? ResponseJsonColumn,
        bool DryRun = false);
}
