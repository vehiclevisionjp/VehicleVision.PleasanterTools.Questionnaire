using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>回答画面から来る要求 1 件。</summary>
public sealed record SubmitRequest(ImmutableArray<PayloadAnswer> Answers);

/// <summary>回答画面へ返す定義。</summary>
/// <remarks>
/// **Pleasanter の <c>ReferenceId</c> やサイト ID を含めない**
/// （<c>_documents/画面設計.md</c> 1 章）。
/// </remarks>
public sealed record FormResponse(string PublicId, SurveyDefinition Definition);

/// <summary>回答画面向けの口。**認証は無い。**</summary>
public static class FormEndpoints
{
    /// <summary>回答トークンの長さ（バイト）。</summary>
    private const int TokenBytes = 24;

    /// <summary>画面と同じ書き方（先頭小文字）で読む。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapFormEndpoints(this IEndpointRouteBuilder app)
    {
        var forms = app.MapGroup("/api/forms");

        forms.MapGet("/{publicId}", async (
            string publicId,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            var (definition, rejection) = await intake.GetPublishedAsync(publicId, cancellationToken);
            return definition is null
                ? ToProblem(rejection)
                : Results.Ok(new FormResponse(publicId, definition));
        });

        forms.MapGet("/{publicId}/responses/{responseToken}", async (
            string publicId,
            string responseToken,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            // **送信待ちを先に見る。** 未送信の回答は Pleasanter にまだ無い
            var pending = await intake.FindPendingAsync(responseToken, cancellationToken);
            return pending is null
                ? Results.NotFound()
                : Results.Ok(new { answers = pending.Answers });
        });

        // **添付は回答と同じ要求で受け取る**（`multipart/form-data`）。
        // 添付だけ先に預かる口を作ると、送信されないまま残った中身の始末が要る。
        // 添付が無いときは今までどおり JSON で送れる
        forms.MapPut("/{publicId}/responses/{responseToken}", async (
            HttpContext context,
            string publicId,
            string responseToken,
            ResponseIntake intake,
            AttachmentOptions attachmentOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!IsWellFormedToken(responseToken))
            {
                return Results.BadRequest();
            }

            // **要求本文の上限を明示する。既定値に任せない**（_documents/非機能設計.md 1 章）
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = attachmentOptions.MaxRequestBodyBytes;
            }

            SubmitRequest? request;
            IReadOnlyList<AnsweredAttachment> attachments;

            try
            {
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync(cancellationToken);
                    request = ReadSubmitRequest(form["answers"]);
                    attachments = await ReadAttachmentsAsync(form.Files, cancellationToken);
                }
                else
                {
                    request = await context.Request.ReadFromJsonAsync<SubmitRequest>(cancellationToken);
                    attachments = [];
                }
            }
            catch (BadHttpRequestException exception)
            {
                // 上限超過（413）と壊れた本文（400）を、そのままの意味で返す
                return Results.StatusCode(exception.StatusCode);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }

            if (request is null)
            {
                return Results.BadRequest();
            }

            var answers = request.Answers
                .Select(answer => new Answer(answer.QuestionId, answer.Values)
                {
                    OtherText = answer.OtherText,
                    FileNames = answer.FileNames.IsDefault ? [] : answer.FileNames,
                })
                .ToArray();

            var result = await intake.SubmitAsync(
                publicId, responseToken, answers, attachments, cancellationToken);

            if (result.Accepted)
            {
                // **受付完了。** Pleasanter へはこの後ワーカーが送る
                return Results.Accepted();
            }

            return result.Rejection switch
            {
                IntakeRejection.Invalid => Results.ValidationProblem(ToValidationErrors(result)),
                IntakeRejection.AttachmentRejected => ToAttachmentProblem(
                    result, loggerFactory.CreateLogger(typeof(FormEndpoints))),
                _ => ToProblem(result.Rejection),
            };
        });

        return app;
    }

    /// <summary>回答トークンを作る。**暗号論的乱数から作る。**</summary>
    /// <remarks>
    /// **これを知っている人はその回答を書き換えられる**
    /// （<c>_documents/アーキテクチャ方針.md</c> 9 章）。
    /// URL・メール・ログへ載せる経路を作らないこと。
    /// </remarks>
    public static string NewResponseToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));

    private static bool IsWellFormedToken(string token) =>
        token.Length == TokenBytes * 2 && token.All(Uri.IsHexDigit);

    /// <summary><c>multipart/form-data</c> の <c>answers</c> 欄を読む。</summary>
    private static SubmitRequest? ReadSubmitRequest(string? answersJson) =>
        string.IsNullOrWhiteSpace(answersJson)
            ? new SubmitRequest([])
            : JsonSerializer.Deserialize<SubmitRequest>(answersJson, JsonOptions);

    /// <summary>添付を読み取る。**欄の名前がどの設問かを表す。**</summary>
    /// <remarks>
    /// **ファイル名は画面が名乗ったまま渡す。** ここで整形すると、
    /// パス区切りを含む名前を弾く検査（<c>AttachmentInspector</c>）が働かなくなる。
    /// </remarks>
    private static async Task<IReadOnlyList<AnsweredAttachment>> ReadAttachmentsAsync(
        IFormFileCollection files,
        CancellationToken cancellationToken)
    {
        var attachments = new List<AnsweredAttachment>(files.Count);

        foreach (var file in files)
        {
            using var buffer = new MemoryStream((int)Math.Max(file.Length, 0));
            await file.CopyToAsync(buffer, cancellationToken);
            attachments.Add(new AnsweredAttachment(
                file.Name, new IncomingAttachment(file.FileName, buffer.ToArray())));
        }

        return attachments;
    }

    /// <summary>添付を受け付けなかったことを応答にする。</summary>
    /// <remarks>
    /// **検出名を回答者へ出さない**（<c>_documents/添付ファイル検査-運用手順書.md</c> 6 章）。
    /// 検出したことも理由としては伏せ、「受け付けられない」とだけ返す。
    /// **スキャナへ到達できないときは 503。** 「スキャンできなかったので通す」にしない。
    /// </remarks>
    private static IResult ToAttachmentProblem(IntakeResult result, ILogger logger)
    {
        // **回答本文と中身は残さない。** 記録するのはファイル名と理由だけ
        foreach (var rejection in result.Attachments)
        {
            logger.LogWarning(
                "添付を受け付けなかった: 理由={Reason} 設問={QuestionId} ファイル={FileName}",
                rejection.Reason,
                rejection.QuestionId,
                rejection.FileName);
        }

        if (result.Attachments.Any(
            rejection => rejection.Reason is AttachmentRejectionReason.ScannerUnavailable))
        {
            // **管理者への通知が要る状態。** 添付を受け付けられないまま運用が続いている
            logger.LogError("ウイルススキャナへ到達できないため添付を受け付けていない");
            return Results.Json(
                new { reason = "scannerUnavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new
            {
                attachments = result.Attachments.Select(rejection => new
                {
                    questionId = rejection.QuestionId,
                    fileName = rejection.FileName,
                    reason = PublicReason(rejection.Reason),
                }),
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>回答者へ返してよい理由へ丸める。</summary>
    private static string PublicReason(AttachmentRejectionReason reason) => reason switch
    {
        // **何を検出したかはもちろん、検出したこと自体も伏せる**
        AttachmentRejectionReason.Infected => "rejected",
        _ => char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..],
    };

    /// <summary>断った理由を応答にする。</summary>
    /// <remarks>
    /// **存在しない公開 ID と未公開を区別しない。**
    /// 区別すると、総当たりで「実在するか」が分かってしまう。
    /// </remarks>
    private static IResult ToProblem(IntakeRejection? rejection) => rejection switch
    {
        IntakeRejection.NotStarted => Results.Json(
            new { reason = "notStarted" }, statusCode: StatusCodes.Status403Forbidden),
        IntakeRejection.Closed => Results.Json(
            new { reason = "closed" }, statusCode: StatusCodes.Status403Forbidden),
        IntakeRejection.Suspended => Results.Json(
            new { reason = "suspended" }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.NotFound(),
    };

    private static Dictionary<string, string[]> ToValidationErrors(IntakeResult result) =>
        result.Errors
            .GroupBy(error => error.QuestionId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Code.ToString()).ToArray(),
                StringComparer.Ordinal);
}
