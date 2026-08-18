using System.Collections.Immutable;
using System.Security.Cryptography;
using VehicleVision.PleasanterTools.Questionnaire.Core.Answers;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>回答画面から来る要求 1 件。</summary>
/// <param name="Ticket">
/// 送信チケット。**画面を開いたときにサーバが発行したものをそのまま返してもらう**
/// （<see cref="SubmissionGuard"/>）。
/// </param>
/// <param name="Trap">
/// ハニーポット項目。**人間には見えないので、埋まっていたら bot。**
/// 画面側の名前と揃えること。
/// </param>
public sealed record SubmitRequest(
    ImmutableArray<PayloadAnswer> Answers, string? Ticket = null, string? Trap = null);

/// <summary>送信チケットの要求。</summary>
/// <param name="ResponseToken">
/// 端末が既に持っている回答トークン。**初めての回答なら <c>null</c>。**
/// **URL ではなく本文で受け取る。** URL に載せると経路のログへ残る
/// （<c>_documents/非機能設計.md</c> 1 章「識別子の秘匿」）。
/// </param>
public sealed record TicketRequest(string? ResponseToken = null);

/// <summary>回答画面へ返す送信チケット。</summary>
public sealed record TicketResponse(string ResponseToken, string Ticket);

/// <summary>回答画面へ返す定義。</summary>
/// <remarks>
/// **Pleasanter の <c>ReferenceId</c> やサイト ID を含めない**
/// （<c>_documents/画面設計.md</c> 1 章）。
/// </remarks>
public sealed record FormResponse(string PublicId, SurveyDefinition Definition);

/// <summary>回答画面向けの口。**認証は無い。**</summary>
public static class FormEndpoints
{
    /// <summary>回答の送信だけに掛けるレート制限の名前。</summary>
    /// <remarks>
    /// **書き込みは読み取りより高くつく。** 全体の枠に紛れさせない
    /// （<c>_documents/非機能設計.md</c> 1 章）。
    /// </remarks>
    public const string SubmitRateLimitPolicy = "form-submit";

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

        // **送信チケットを出す。** 画面を開いた時刻を署名に閉じ込めて返すだけで、
        // サーバ側には何も覚えない（回答者を突き合わせる手掛かりを残さない）。
        //
        // **DB を見ない。** 見ると応答の速さで公開 ID の実在が分かってしまうため、
        // 存在しないアンケートでも同じようにチケットを返す。**使っても受付で断られる。**
        forms.MapPost("/{publicId}/ticket", (
            string publicId,
            TicketRequest request,
            SubmissionGuard guard) =>
        {
            // **端末が持っていない・書式が壊れていれば、こちらで作る。**
            // 回答トークンは推測不能でなければならない値なので、
            // 画面任せにせずサーバの暗号論的乱数から出す
            var responseToken = request.ResponseToken is { } token && IsWellFormedToken(token)
                ? token
                : NewResponseToken();

            return Results.Ok(new TicketResponse(responseToken, guard.Issue(publicId, responseToken)));
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
            SubmissionGuard guard,
            ILogger<SubmissionGuard> logger,
            CancellationToken cancellationToken) =>
        {
            if (!IsWellFormedToken(responseToken))
            {
                return Results.BadRequest();
            }

            // **DB を見る前に bot を弾く**（_documents/非機能設計.md 1 章）。
            // 書いてから弾いても、DB の消費はもう起きている。
            // **アンケートの実在を確かめる前に弾くので、ここで断った応答は
            // 公開 ID が実在するかを漏らさない。**
            var botRejection = guard.Check(request.Ticket, request.Trap, publicId, responseToken);
            if (botRejection is { } reason)
            {
                // **理由は外へ返さない。** 返すと、bot がどこを直せばよいか分かる。
                // 記録は残す（回答本文とトークンは書かない）
                logger.LogWarning("回答の送信を bot 対策で断った。理由: {Reason}", reason);
                return Results.Json(
                    new { reason = "rejected" }, statusCode: StatusCodes.Status403Forbidden);
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
        }).RequireRateLimiting(SubmitRateLimitPolicy);

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
