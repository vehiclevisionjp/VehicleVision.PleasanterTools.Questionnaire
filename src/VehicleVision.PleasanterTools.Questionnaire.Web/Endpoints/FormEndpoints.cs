using System.Collections.Immutable;
using System.Security.Cryptography;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

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

        forms.MapPut("/{publicId}/responses/{responseToken}", async (
            string publicId,
            string responseToken,
            SubmitRequest request,
            ResponseIntake intake,
            CancellationToken cancellationToken) =>
        {
            if (!IsWellFormedToken(responseToken))
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

            var result = await intake.SubmitAsync(publicId, responseToken, answers, cancellationToken);

            if (result.Accepted)
            {
                // **受付完了。** Pleasanter へはこの後ワーカーが送る
                return Results.Accepted();
            }

            return result.Rejection is IntakeRejection.Invalid
                ? Results.ValidationProblem(ToValidationErrors(result))
                : ToProblem(result.Rejection);
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
