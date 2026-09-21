using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>システム全体のメンテナンス状態を管理する入口。</summary>
public static class AdminMaintenanceEndpoints
{
    private const int MessageLength = 500;

    public static IEndpointRouteBuilder MapAdminMaintenanceEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/maintenance")
            .WithTags("管理 API")
            .RequireAuthorization(AdminAuthSchemes.SessionPolicy);

        AdminAuthSchemes.AddNoStore(group);
        group.AddEndpointFilter<AuditLogFilter>();

        group.MapGet("/", async (
            MaintenanceMode maintenance,
            CancellationToken cancellationToken) =>
            Results.Ok(await maintenance.GetStatusAsync(cancellationToken).ConfigureAwait(false)));

        group.MapPut("/", async (
            MaintenanceModeRequest request,
            HttpContext context,
            MaintenanceMode maintenance,
            CancellationToken cancellationToken) =>
        {
            if (request.MessageJa?.Length > MessageLength
                || request.MessageEn?.Length > MessageLength)
            {
                return Results.BadRequest(new { message = "メッセージは500文字以内で入力してください。" });
            }

            if (!Guid.TryParse(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    out var adminUserId))
            {
                return Results.Unauthorized();
            }

            AuditNotes.SetTarget(context, "MaintenanceMode", "System");
            AuditNotes.Add(context, "enabled", request.Enabled.ToString());

            var status = await maintenance.SetDatabaseAsync(
                request.Enabled,
                request.MessageJa,
                request.MessageEn,
                adminUserId,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        }).RequireAuthorization(
            AdminPermissions.PolicyOf(AdminPermissions.MaintenanceManage));

        return builder;
    }
}

/// <summary>DB 側のメンテナンス状態を変更する要求。</summary>
public sealed record MaintenanceModeRequest(
    bool Enabled,
    string? MessageJa,
    string? MessageEn);
