using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>専用 API キーの登録・再入力の本文。</summary>
public sealed record LookupCredentialRequest(string? ApiKey);

/// <summary>失効・再開の本文。</summary>
public sealed record LookupCredentialEnabledRequest(bool? Enabled);

/// <summary>接続試験・列の取得の本文。</summary>
public sealed record LookupInspectRequest(string? CredentialId, long? SourceSiteId);

/// <summary>管理プレビューの検索の本文。**保存済みの下書きのルックアップだけを使う。**</summary>
public sealed record LookupPreviewSearchRequest(Dictionary<string, string?>? Keys);

/// <summary>資格情報の状態の応答。**秘密（平文も暗号文）は含めない。**</summary>
public sealed record LookupCredentialStatusResponse(
    string CredentialId,
    string SourceKind,
    bool IsEnabled,
    DateTime UpdatedAt,
    string? UpdatedBy)
{
    public static LookupCredentialStatusResponse From(LookupCredentialStatus status) =>
        new(status.CredentialId, status.SourceKind.ToString(), status.IsEnabled, status.UpdatedAt, status.UpdatedBy);
}

/// <summary>参照元の列 1 本の応答（管理者向け。名前と種類だけ）。</summary>
public sealed record LookupInspectColumn(
    string Name,
    string Kind,
    string? Label,
    bool HasChoices,
    bool Nullable,
    int? DecimalPlaces);

/// <summary>接続試験の応答。**分類・統合/継承の有無・列の名前と種類だけ。** 値・生の応答・URL は返さない。</summary>
public sealed record LookupInspectResponse(
    bool Ok,
    string? Failure,
    bool IsIntegrated,
    bool InheritsPermission,
    IReadOnlyList<LookupInspectColumn> Columns);

/// <summary>
/// ルックアップの管理 API（Issue #879・仕様書 8 章・U7・U9）。
/// </summary>
/// <remarks>
/// <para>
/// **秘密（API キー）は本文で受けるだけ。** 応答・ログ・監査には出さない
/// （<see cref="AuditLogFilter"/> は要求本文に触らない）。状態は「設定済みの有無・有効状態・更新日時」だけ返す。
/// </para>
/// <para>
/// **設定・API キー・接続試験は <see cref="AdminPermissions.LookupsManage"/>（アンケート管理者）だけ**（U7）。
/// プレビュー検索は <see cref="AdminPermissions.SurveysWrite"/>。公開 API（<c>/api/forms/...</c>）とは別経路で、
/// **下書きを使えるのはここだけ。**
/// </para>
/// </remarks>
public static class AdminLookupEndpoints
{
    /// <summary>API キーの最大長。想定外に長い値を保存・暗号化しない。</summary>
    private const int MaxApiKeyLength = 512;

    public static IEndpointRouteBuilder MapAdminLookupEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/surveys/{surveyId:guid}/lookups")
            .WithTags("管理 API")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysRead));
        group.AddEndpointFilter<AuditLogFilter>();
        group.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            return await next(context);
        });

        var manage = AdminPermissions.PolicyOf(AdminPermissions.LookupsManage);

        group.MapGet("/credentials", async (
            Guid surveyId,
            ISurveyRepository surveys,
            ILookupCredentialStore credentials,
            CancellationToken cancellationToken) =>
        {
            if (await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }

            var list = await credentials.ListStatusAsync(surveyId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(list.Select(LookupCredentialStatusResponse.From));
        });

        group.MapPost("/credentials", async (
            Guid surveyId,
            LookupCredentialRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ILookupCredentialStore credentials,
            CancellationToken cancellationToken) =>
        {
            var problem = await CheckWritableAsync(surveyId, context, surveys, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }

            if (!TryApiKey(request, out var apiKey))
            {
                return InvalidKey(context);
            }

            var id = await credentials
                .CreateAsync(surveyId, LookupSourceKind.Pleasanter, apiKey, UserOf(context), cancellationToken)
                .ConfigureAwait(false);
            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "credentialId", id);
            var status = await credentials.FindStatusAsync(surveyId, id, cancellationToken).ConfigureAwait(false);
            return Results.Ok(LookupCredentialStatusResponse.From(status!));
        }).RequireAuthorization(manage);

        group.MapPut("/credentials/{credentialId}", async (
            Guid surveyId,
            string credentialId,
            LookupCredentialRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ILookupCredentialStore credentials,
            CancellationToken cancellationToken) =>
        {
            var problem = await CheckWritableAsync(surveyId, context, surveys, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                return problem;
            }

            if (!TryApiKey(request, out var apiKey))
            {
                return InvalidKey(context);
            }

            if (!await credentials
                .RotateAsync(surveyId, credentialId, apiKey, UserOf(context), cancellationToken)
                .ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "credentialId", credentialId);
            var status = await credentials.FindStatusAsync(surveyId, credentialId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(LookupCredentialStatusResponse.From(status!));
        }).RequireAuthorization(manage);

        // 失効・再開。**公開し直さずに全公開版の実行時へ効く**
        group.MapPut("/credentials/{credentialId}/enabled", async (
            Guid surveyId,
            string credentialId,
            LookupCredentialEnabledRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ILookupCredentialStore credentials,
            CancellationToken cancellationToken) =>
        {
            if (request.Enabled is not { } enabled)
            {
                return Results.BadRequest();
            }

            if (await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }

            if (!await credentials
                .SetEnabledAsync(surveyId, credentialId, enabled, UserOf(context), cancellationToken)
                .ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "credentialId", credentialId);
            AuditNotes.Add(context, "enabled", enabled ? "true" : "false");
            var status = await credentials.FindStatusAsync(surveyId, credentialId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(LookupCredentialStatusResponse.From(status!));
        }).RequireAuthorization(manage);

        group.MapDelete("/credentials/{credentialId}", async (
            Guid surveyId,
            string credentialId,
            HttpContext context,
            ISurveyRepository surveys,
            ILookupCredentialStore credentials,
            CancellationToken cancellationToken) =>
        {
            if (await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }

            if (!await credentials.DeleteAsync(surveyId, credentialId, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "credentialId", credentialId);
            return Results.NoContent();
        }).RequireAuthorization(manage);

        // 接続試験と列の取得。**読み取りだけ（GetSite）。** 結果は分類・統合/継承の有無・列の名前と種類だけ
        group.MapPost("/inspect", async (
            Guid surveyId,
            LookupInspectRequest request,
            HttpContext context,
            ISurveyRepository surveys,
            ILookupSiteInspectionService inspector,
            CancellationToken cancellationToken) =>
        {
            if (await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.CredentialId) || request.SourceSiteId is not > 0)
            {
                return Results.BadRequest();
            }

            LookupSiteInspection inspection;
            try
            {
                inspection = await inspector
                    .InspectAsync(surveyId, request.CredentialId, request.SourceSiteId.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PleasanterConfigurationException)
            {
                inspection = new LookupSiteInspection
                {
                    Status = LookupSearchStatus.SourceInvalid,
                    Failure = LookupSearchFailure.InvalidDefinition,
                };
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "result", inspection.IsSuccess ? "ok" : inspection.Failure.ToString());
            return Results.Ok(inspection.IsSuccess
                ? new LookupInspectResponse(
                    true,
                    null,
                    inspection.IsIntegrated,
                    inspection.InheritsPermission,
                    [.. inspection.Columns.Select(column => new LookupInspectColumn(
                        column.Name,
                        column.Kind.ToString(),
                        column.Label,
                        column.HasChoices,
                        column.Nullable,
                        column.DecimalPlaces))])
                : new LookupInspectResponse(
                    false,
                    inspection.Failure == LookupSearchFailure.None
                        ? inspection.Status.ToString()
                        : inspection.Failure.ToString(),
                    false,
                    false,
                    []));
        }).RequireAuthorization(manage);

        // 公開前の検査（保存済みの下書き）。テスト公開と同じ検証
        group.MapGet("/problems", async (
            Guid surveyId,
            ISurveyDraftStore drafts,
            LookupPublishValidator validator,
            CancellationToken cancellationToken) =>
        {
            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            if (draft is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await validator.ValidateAsync(surveyId, draft.Definition, cancellationToken)
                .ConfigureAwait(false));
        }).RequireAuthorization(manage);

        // 管理プレビューの検索。**公開 API とは別経路。** 保存済みの下書きのルックアップだけを使い、
        // 回答者向けのトークン・枠・公開版の解決を通らない（認証・権限つき）
        group.MapPost("/{lookupId}/preview-search", async (
            Guid surveyId,
            string lookupId,
            LookupPreviewSearchRequest request,
            HttpContext context,
            ISurveyDraftStore drafts,
            ILookupSearchService search,
            CancellationToken cancellationToken) =>
        {
            if (request.Keys is not { Count: > 0 and <= LookupLimits.MaxKeys })
            {
                return Results.BadRequest(new { code = "LookupInvalidKeys" });
            }

            var draft = await drafts.LoadAsync(surveyId, cancellationToken).ConfigureAwait(false);
            var lookup = draft?.Definition.GetLookups()
                .FirstOrDefault(item => string.Equals(item.LookupId, lookupId, StringComparison.Ordinal));
            if (draft is null || lookup is null)
            {
                return Results.NotFound();
            }

            LookupSearchResult result;
            try
            {
                result = await search.SearchAsync(surveyId, lookup, request.Keys, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PleasanterConfigurationException)
            {
                return Results.Json(new { code = "LookupSourceInvalid" }, statusCode: StatusCodes.Status502BadGateway);
            }

            AuditNotes.SetTarget(context, "survey", surveyId.ToString());
            AuditNotes.Add(context, "lookupId", lookupId);
            AuditNotes.Add(context, "status", result.Status.ToString());

            // 失敗は分類（コード）だけ。管理者の切り分け用に詳細な分類（failure）も添える。値は返さない
            return result.Status switch
            {
                LookupSearchStatus.Success => Results.Ok(
                    LookupResponseBuilder.Build(draft.Definition.Version, draft.Definition, lookup, result)),
                LookupSearchStatus.InvalidKeys => Results.BadRequest(new
                {
                    code = "LookupInvalidKeys",
                    fieldIds = result.InvalidKeyQuestionIds,
                }),
                LookupSearchStatus.TooManyResults => Results.Json(
                    new { code = "LookupTooManyResults" }, statusCode: StatusCodes.Status422UnprocessableEntity),
                LookupSearchStatus.TemporarilyUnavailable => Results.Json(
                    new { code = "LookupTemporarilyUnavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                LookupSearchStatus.Timeout => Results.Json(
                    new { code = "LookupTimeout" }, statusCode: StatusCodes.Status504GatewayTimeout),
                _ => Results.Json(
                    new { code = "LookupSourceInvalid", failure = result.Failure.ToString() },
                    statusCode: StatusCodes.Status502BadGateway),
            };
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SurveysWrite));

        return builder;
    }

    private static string? UserOf(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static bool TryApiKey(LookupCredentialRequest request, out string apiKey)
    {
        apiKey = request.ApiKey?.Trim() ?? string.Empty;
        return apiKey.Length is > 0 and <= MaxApiKeyLength && !apiKey.Any(char.IsControl);
    }

    private static IResult InvalidKey(HttpContext context) =>
        Results.BadRequest(new
        {
            message = ServerMessages.Get(
                ServerMessageKeys.LookupCredentialInvalid, RequestLanguage.Of(context)),
        });

    /// <summary>資格情報を書き込める状態か（存在・アーカイブ・テンプレート）。</summary>
    private static async Task<IResult?> CheckWritableAsync(
        Guid surveyId,
        HttpContext context,
        ISurveyRepository surveys,
        CancellationToken cancellationToken)
    {
        var survey = await surveys.FindBySurveyIdAsync(surveyId, cancellationToken).ConfigureAwait(false);
        if (survey is null)
        {
            return Results.NotFound();
        }

        if (survey.ArchivedAt is not null)
        {
            return Results.Conflict(new
            {
                message = ServerMessages.Get(ServerMessageKeys.SurveyArchived, RequestLanguage.Of(context)),
            });
        }

        // テンプレートは公開できず、複製では資格情報を引き継がない。ここへ秘密を置かない
        if (survey.IsTemplate)
        {
            return Results.BadRequest(new
            {
                message = ServerMessages.Get(ServerMessageKeys.SurveyIsTemplate, RequestLanguage.Of(context)),
            });
        }

        return null;
    }
}
