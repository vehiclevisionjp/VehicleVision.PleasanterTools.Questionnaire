using System.Collections.Immutable;
using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
using VehicleVision.PleasanterTools.Questionnaire.Pleasanter;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

public sealed record AutoReplyPreviewRequest(SurveyDefinition Definition, string? Language);

public sealed record AutoReplyPreviewResponse(
    string FromAddress,
    string? FromName,
    string ToAddress,
    string? ReplyToAddress,
    string? BccAddress,
    string Subject,
    string Body,
    ImmutableArray<string> UnknownKeywords);

/// <summary>保存前の自動返信を、本番と同じ合成処理で確かめる口（Issue #319）。</summary>
/// <remarks>
/// **監査には残さない。** 編集中に繰り返し呼ばれ、DB も状態も変更しない。
/// </remarks>
public static class AdminAutoReplyEndpoints
{
    public const string TestSendRateLimitPolicy = "auto-reply-test-send";

    private const string PreviewPublicId = "preview";
    private const string PreviewToken = "preview";
    private const string PreviewAddress = "preview@example.invalid";

    public static IEndpointRouteBuilder MapAdminAutoReplyEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/auto-reply")
            .WithTags("管理 API")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(AdminAuthSchemes.Session)
                .RequireAuthenticatedUser());

        group.MapPost("/preview", (
            AutoReplyPreviewRequest request,
            MailOptions options,
            PleasanterOptions pleasanter,
            TimeProvider timeProvider) =>
        {
            var response = Preview(
                request.Definition,
                request.Language,
                options,
                pleasanter,
                timeProvider.GetUtcNow());
            return Results.Ok(response);
        });

        group.MapPost("/test-send", async (
            AutoReplyPreviewRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AutoReplyTestMailer mailer,
            CancellationToken cancellationToken) =>
        {
            AuditNotes.SetTarget(context, "Survey", request.Definition.SurveyId);

            var outcome = await mailer.TryEnqueueAsync(
                request.Definition,
                request.Language,
                principal.Identity?.Name,
                cancellationToken).ConfigureAwait(false);

            return outcome switch
            {
                AutoReplyTestMailOutcome.Queued => Results.Ok(new { queued = true }),
                AutoReplyTestMailOutcome.LoginIdNotEmail => Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.AutoReplyTestLoginIdNotEmail,
                        RequestLanguage.Of(context)),
                }),
                AutoReplyTestMailOutcome.MailDisabled => Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.AutoReplyTestMailDisabled,
                        RequestLanguage.Of(context)),
                }),
                _ => Results.Problem(
                    ServerMessages.Get(
                        ServerMessageKeys.AutoReplyTestQueueFailed,
                        RequestLanguage.Of(context)),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        })
            .AddEndpointFilter<AuditLogFilter>()
            .RequireRateLimiting(TestSendRateLimitPolicy);

        return builder;
    }

    internal static AutoReplyPreviewResponse Preview(
        SurveyDefinition definition,
        string? language,
        MailOptions options,
        PleasanterOptions pleasanter,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pleasanter);

        var settings = definition.AutoReply;
        var subjectTemplate = settings?.Subject?.Get(language) ?? string.Empty;
        var bodyTemplate = settings?.Body?.Get(language) ?? string.Empty;
        var unknown = AutoReplyKeywords.UnknownIn(subjectTemplate, bodyTemplate);
        var displayNow = TimeZoneInfo.ConvertTime(now, DisplayTimeZone(pleasanter));
        var baseUrl = options.BaseUrl is { Length: > 0 }
            ? options.BaseUrl
            : "https://example.invalid";
        var formUrl = $"{baseUrl.TrimEnd('/')}/f/{PreviewPublicId}";

        var canEdit = definition.AllowEditingAfterSubmit
            && settings is not null
            && settings.EditLinkDays is >= 1 and <= AutoReplySettings.MaxEditLinkDays;
        var editExpiresAt = canEdit ? displayNow.AddDays(settings!.EditLinkDays) : (DateTimeOffset?)null;
        var canReceiveAssets =
            SurveyAssetReferences.HasTicketedAssets(definition)
            && definition.AssetDelivery?.Expiration is not AssetTicketExpiration.CompletedOnly;
        var assetExpiresAt = canReceiveAssets
            ? TimeZoneInfo.ConvertTime(
                new DateTimeOffset(
                    (definition.AssetDelivery ?? new AssetDeliverySettings())
                        .ExpiresAt(now.UtcDateTime, null),
                    TimeSpan.Zero),
                DisplayTimeZone(pleasanter))
            : (DateTimeOffset?)null;

        var values = new AutoReplyPlaceholderValues(
            Answers: null,
            FormUrl: formUrl,
            EditUrl: canEdit
                ? ResponseEditLink.UrlOf(baseUrl, PreviewPublicId, PreviewToken)
                : null,
            EditUrlExpiresAt: editExpiresAt,
            AssetsUrl: canReceiveAssets
                ? AssetTicket.UrlOf(baseUrl, PreviewPublicId, PreviewToken)
                : null,
            AssetsUrlExpiresAt: assetExpiresAt);

        var mail = AutoReplyComposer.Compose(
            definition,
            SamplePayload(definition, language),
            language,
            displayNow,
            values);

        var composed = mail ?? new OutgoingMail(PreviewAddress, string.Empty, string.Empty);
        var headers = MailMessageFactory.ResolveHeaders(options, composed);
        return new AutoReplyPreviewResponse(
            headers.FromAddress,
            headers.FromName,
            headers.ToAddress,
            headers.ReplyToAddress,
            headers.BccAddress,
            mail?.Subject ?? string.Empty,
            mail?.Body ?? string.Empty,
            unknown);
    }

    private static ResponsePayload SamplePayload(SurveyDefinition definition, string? language)
    {
        var answers = definition.AllQuestions
            .Where(question => !question.IsDisplayOnly)
            .Select(question =>
            {
                if (string.Equals(
                    question.QuestionId,
                    definition.AutoReply?.ToQuestionId,
                    StringComparison.Ordinal))
                {
                    return new PayloadAnswer(question.QuestionId, [PreviewAddress]);
                }

                var value = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                    ? "Sample answer"
                    : "見本の回答";
                return question.Type is QuestionType.File
                    ? new PayloadAnswer(question.QuestionId, [], FileNames: ["sample.txt"])
                    : new PayloadAnswer(question.QuestionId, [value]);
            })
            .ToImmutableArray();
        return new ResponsePayload("preview", answers);
    }

    private static TimeZoneInfo DisplayTimeZone(PleasanterOptions options)
    {
        if (options.ApiKeyUserTimeZoneId is not { Length: > 0 } id)
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception)
            when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
