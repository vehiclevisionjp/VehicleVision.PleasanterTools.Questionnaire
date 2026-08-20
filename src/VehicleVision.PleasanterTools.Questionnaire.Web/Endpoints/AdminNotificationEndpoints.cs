using VehicleVision.PleasanterTools.Questionnaire.Core.Notifications;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理者への知らせを読む入口（Issue #80）。</summary>
/// <remarks>
/// <para>
/// **異常はログにしか出ていなかった**（デッドレター・滞留での受付停止・Pleasanter の
/// 認証失敗・回答数の上限）。ログを見張っていない運用では誰も気付けないので、
/// **本アプリの DB へ溜めて、管理画面を開いたときに未読として目に入るようにする。**
/// </para>
/// <para>
/// **Pleasanter を経由しない。** 知らせの多くは「Pleasanter へ届かない」事象そのもので、
/// 届け先に Pleasanter を選ぶと、肝心なときに届かない。
/// </para>
/// <para>
/// **Administrator だけが触れる。** どのアンケートが詰まっているかは、
/// Editor へ開く情報ではない。
/// </para>
/// <para>
/// ⚠️ **回答本文も <c>ResponseToken</c> も返さない**
/// （<see cref="AdminNotificationView"/> がそもそも持たない）。
/// </para>
/// </remarks>
public static class AdminNotificationEndpoints
{
    /// <summary>1 度に返す上限。**画面から大きな値を指定されても超えない。**</summary>
    private const int MaxLimit = 200;

    private const int DefaultLimit = 50;

    public static IEndpointRouteBuilder MapAdminNotificationEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/notifications")
            .RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

        AdminAuthSchemes.AddNoStore(group);

        // **既読にした操作を残す**（読み取りは残さない。AuditLogFilter）
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- 一覧と未読件数 --------------------------------------------------
        group.MapGet("", async (
            IAdminNotificationStore store,
            CancellationToken cancellationToken,
            int? limit = null,
            int? offset = null,
            bool? unreadOnly = null) =>
        {
            // **もう 1 件多く読む。** 次のページがあるかを数え直さずに知るため
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var rows = await store.ListAsync(
                new AdminNotificationQuery
                {
                    Limit = take + 1,
                    Offset = Math.Max(offset ?? 0, 0),
                    UnreadOnly = unreadOnly ?? false,
                },
                cancellationToken).ConfigureAwait(false);

            // **未読件数は一覧と別に数える。** ページを送っても数字は変わらない
            var unread = await store.UnreadCountAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(ToResponse(rows, take, unread));
        });

        // ---- すべて既読にする ------------------------------------------------
        group.MapPost("/read", async (
            IAdminNotificationStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            // **既読は全体で 1 つ。** 誰かが気付いたら全員にとって既読
            var affected = await store
                .MarkAllReadAsync(time.GetUtcNow().UtcDateTime, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { read = affected });
        });

        return builder;
    }

    /// <summary>知らせの 1 ページを応答の形にする。</summary>
    /// <param name="rows">1 件多く読んだ行。</param>
    /// <param name="take">実際に返す件数。</param>
    /// <param name="unreadCount">未読の合計（行数ではなく起きた回数）。</param>
    public static AdminNotificationPageResponse ToResponse(
        IReadOnlyList<AdminNotificationView> rows,
        int take,
        int unreadCount)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new AdminNotificationPageResponse(
            [.. rows.Take(take).Select(ToResponse)],
            rows.Count > take,
            unreadCount);
    }

    /// <summary>知らせ 1 件を応答の形にする。</summary>
    /// <remarks>
    /// **時刻に UTC の印を付ける。** DB の列は時間帯を持たないので、
    /// そのまま返すと画面が端末の時間帯として読む。
    /// **種類は文字列で返す。** 画面が整数の意味を知っている状態にしない。
    /// </remarks>
    public static AdminNotificationResponse ToResponse(AdminNotificationView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AdminNotificationResponse(
            row.AdminNotificationId,
            KindName(row.Kind),
            // **紐づかない知らせは Guid.Empty で入っている。** 画面へは無い物として返す
            row.SurveyId == Guid.Empty ? null : row.SurveyId,
            row.SurveyTitle,
            row.Count,
            DbTime.AsUtc(row.FirstOccurredAt),
            DbTime.AsUtc(row.LastOccurredAt),
            DbTime.AsUtc(row.ReadAt));
    }

    /// <summary>DB の整数を画面が読む名前にする。</summary>
    /// <remarks>
    /// **知らない値は <c>unknown</c> にして落とさない。** 古い版のアプリが
    /// 新しい種類の知らせを読んでも、一覧全体が開けなくなるより良い。
    /// </remarks>
    private static string KindName(int kind) =>
        Enum.IsDefined(typeof(AdminNotificationKind), kind)
            ? ((AdminNotificationKind)kind).ToString()
            : "Unknown";
}

/// <summary>知らせの 1 ページ。</summary>
/// <param name="HasMore">次のページがあるか。</param>
/// <param name="UnreadCount">未読の合計。**行数ではなく起きた回数。**</param>
public sealed record AdminNotificationPageResponse(
    IReadOnlyList<AdminNotificationResponse> Items,
    bool HasMore,
    int UnreadCount);

/// <summary>知らせ 1 件。</summary>
/// <param name="Kind">種類の名前（<c>DeadLettered</c> など）。</param>
/// <param name="SurveyId">紐づくアンケート。**無ければ返らない。**</param>
/// <param name="ReadAt">既読の時刻。**未読なら返らない。**</param>
public sealed record AdminNotificationResponse(
    Guid Id,
    string Kind,
    Guid? SurveyId,
    string? SurveyTitle,
    int Count,
    DateTime FirstOccurredAt,
    DateTime LastOccurredAt,
    DateTime? ReadAt);
