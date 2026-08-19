using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理操作の記録を読む入口。</summary>
/// <remarks>
/// <para>
/// **Administrator だけが読める。** 誰が何をしたかは、Editor へ見せる情報ではない。
/// </para>
/// <para>
/// **この入口を叩いた記録は残さない**（読み取りは残さない方針。<c>AuditLogFilter</c>）。
/// 一覧を開くたびに行が増えると、本当に見たいもの（変えた操作）が埋もれる。
/// </para>
/// <para>
/// **絞り込みはサーバで行う。** 全件返して画面で絞ると、
/// 増え続ける表を毎回そのまま送ることになる。
/// </para>
/// </remarks>
public static class AdminAuditLogEndpoints
{
    /// <summary>1 度に返す上限。**画面から大きな値を指定されても超えない。**</summary>
    private const int MaxLimit = 200;

    private const int DefaultLimit = 50;

    public static IEndpointRouteBuilder MapAdminAuditLogEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/audit-logs")
            .RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

        AdminAuthSchemes.AddNoStore(group);

        group.MapGet("", async (
            IAuditLogStore store,
            CancellationToken cancellationToken,
            int? limit = null,
            int? offset = null,
            DateTime? from = null,
            DateTime? to = null,
            Guid? adminUserId = null,
            bool failedOnly = false,
            string? action = null) =>
        {
            // **もう 1 件多く読む。** 次のページがあるかを数え直さずに知るため
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var rows = await store.ListAsync(
                new AuditLogQuery
                {
                    Limit = take + 1,
                    Offset = Math.Max(offset ?? 0, 0),
                    From = from,
                    To = to,
                    AdminUserId = adminUserId,
                    FailedOnly = failedOnly,
                    ActionContains = action,
                },
                cancellationToken).ConfigureAwait(false);

            var hasMore = rows.Count > take;

            return Results.Ok(new
            {
                entries = rows.Take(take).Select(row => new
                {
                    occurredAt = row.OccurredAt,
                    adminUserId = row.AdminUserId,
                    adminLoginId = row.AdminLoginId,
                    action = row.Action,
                    statusCode = row.StatusCode,
                    targetType = row.TargetType,
                    targetId = row.TargetId,
                    detail = row.DetailJson,
                    ipAddress = row.IpAddress,
                }),
                hasMore,
            });
        });

        return builder;
    }
}
