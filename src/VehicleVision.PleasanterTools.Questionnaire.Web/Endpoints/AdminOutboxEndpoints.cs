using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>送信状況を見て、デッドレターを手で送り直す入口。</summary>
/// <remarks>
/// <para>
/// **回答が Pleasanter へ届かず溜まっていることに、人が気付けるようにするためのもの**
/// （Issue #45、<c>_documents/画面設計.md</c> 2 章）。
/// </para>
/// <para>
/// **Administrator だけが触れる。** 届いていない回答の存在も、
/// 送り直すという操作も、Editor へ開く情報ではない。
/// </para>
/// <para>
/// **回答本文を返さない。** <c>PayloadJson</c> には個人情報が入り得る
/// （<c>_documents/データモデル設計.md</c> 2.5）。返すのは失敗の理由と滞留の状況だけで、
/// **そもそも読み取りの型（<see cref="DeadLetterView"/>）が本文を持たない。**
/// </para>
/// <para>
/// **自動で再送し続けない。** デッドレターは、原因を直した人が判断して戻すもの。
/// </para>
/// </remarks>
public static class AdminOutboxEndpoints
{
    /// <summary>1 度に返す上限。**画面から大きな値を指定されても超えない。**</summary>
    private const int MaxLimit = 200;

    private const int DefaultLimit = 50;

    /// <summary>監査ログに残す対象の種類。**回答ではなくアンケートで残す。**</summary>
    /// <remarks>
    /// **<c>ResponseToken</c> は監査ログへ入れない**
    /// （<c>_documents/データモデル設計.md</c> 2.6）。
    /// どの回答かではなく、**どのアンケートの回答を戻したか**を残す。
    /// </remarks>
    private const string AuditTargetType = "Survey";

    public static IEndpointRouteBuilder MapAdminOutboxEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/outbox")
            .RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

        AdminAuthSchemes.AddNoStore(group);

        // **手で戻した操作を残す**（読み取りは残さない。AuditLogFilter）
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- 滞留の状況 ------------------------------------------------------
        group.MapGet("/status", async (
            IResponseOutbox outbox,
            CancellationToken cancellationToken) =>
        {
            var status = await outbox.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResponse(status));
        });

        // ---- デッドレターの一覧 ----------------------------------------------
        group.MapGet("/dead-letters", async (
            IResponseOutbox outbox,
            CancellationToken cancellationToken,
            int? limit = null,
            int? offset = null) =>
        {
            // **もう 1 件多く読む。** 次のページがあるかを数え直さずに知るため
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var rows = await outbox.ListDeadLettersAsync(
                new DeadLetterQuery
                {
                    Limit = take + 1,
                    Offset = Math.Max(offset ?? 0, 0),
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(ToResponse(rows, take));
        });

        // ---- 送信待ちへ戻す --------------------------------------------------
        group.MapPost("/dead-letters/requeue", async (
            RequeueRequest request,
            HttpContext context,
            IResponseOutbox outbox,
            CancellationToken cancellationToken) =>
        {
            // **回答トークンは経路ではなく本文で受け取る。**
            // 経路に出すと AuditLogFilter が経路の値として監査ログへ書いてしまう
            // （`ResponseToken` は監査ログへ入れない決まり）
            if (string.IsNullOrWhiteSpace(request?.ResponseToken))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.ResponseTokenRequired, RequestLanguage.Of(context)),
                });
            }

            var surveyId = await outbox
                .RequeueDeadLetterAsync(request.ResponseToken.Trim(), cancellationToken)
                .ConfigureAwait(false);

            if (surveyId is null)
            {
                return Results.NotFound(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.DeadLetterNotFound, RequestLanguage.Of(context)),
                });
            }

            // **何を戻したかが後から分かるようにする**（Issue #45）。
            // 経路の値からは分からないので、入口が明示して預ける
            AuditNotes.SetTarget(context, AuditTargetType, surveyId.Value.ToString());

            return Results.Ok(new { requeued = true });
        });

        return builder;
    }

    /// <summary>滞留の状況を応答の形にする。</summary>
    /// <remarks>
    /// **時刻に UTC の印を付ける。** DB の列は時間帯を持たないので、
    /// そのまま返すと画面が端末の時間帯として読む（<see cref="DbTime.AsUtc(DateTime?)"/>）。
    /// </remarks>
    public static OutboxStatusResponse ToResponse(OutboxStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new OutboxStatusResponse(
            status.PendingCount,
            DbTime.AsUtc(status.OldestPendingAt),
            status.DeadLetterCount,
            DbTime.AsUtc(status.OldestDeadLetterAt));
    }

    /// <summary>デッドレターの一覧を応答の形にする。</summary>
    /// <param name="rows">1 件多く読んだ行。</param>
    /// <param name="take">実際に返す件数。</param>
    public static DeadLetterPageResponse ToResponse(IReadOnlyList<DeadLetterView> rows, int take)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new DeadLetterPageResponse(
            [.. rows.Take(take).Select(ToResponse)],
            rows.Count > take);
    }

    /// <summary>デッドレター 1 件を応答の形にする。**回答本文は載らない。**</summary>
    public static DeadLetterResponse ToResponse(DeadLetterView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new DeadLetterResponse(
            row.ResponseToken,
            row.SurveyId,
            row.SurveyTitle,
            row.SurveyVersion,
            row.RetryCount,
            row.LastError,
            DbTime.AsUtc(row.CreatedAt),
            DbTime.AsUtc(row.UpdatedAt));
    }

    /// <summary>デッドレターを送信待ちへ戻す要求。</summary>
    public sealed record RequeueRequest(string? ResponseToken);
}

/// <summary>送信状況の応答。</summary>
/// <param name="OldestPendingAt">
/// 最も古い滞留の受付時刻。**1 件も無ければ返らない**（null は落として返す）。
/// </param>
public sealed record OutboxStatusResponse(
    int PendingCount,
    DateTime? OldestPendingAt,
    int DeadLetterCount,
    DateTime? OldestDeadLetterAt);

/// <summary>デッドレター 1 件の応答。</summary>
/// <remarks>
/// **回答本文の入る場所が無い。** 約束を注意書きではなく型で守る。
/// </remarks>
/// <param name="ReceivedAt">回答を受け付けた時刻。**ここからずっと届いていない。**</param>
/// <param name="LastAttemptAt">最後に試した時刻。</param>
public sealed record DeadLetterResponse(
    string ResponseToken,
    Guid SurveyId,
    string? SurveyTitle,
    int SurveyVersion,
    int RetryCount,
    string? LastError,
    DateTime ReceivedAt,
    DateTime LastAttemptAt);

/// <summary>デッドレターの 1 ページ。</summary>
/// <param name="HasMore">
/// 次のページがあるか。**総数は数えない**（増え続ける表を毎回数えないため）。
/// </param>
public sealed record DeadLetterPageResponse(
    IReadOnlyList<DeadLetterResponse> Entries,
    bool HasMore);
