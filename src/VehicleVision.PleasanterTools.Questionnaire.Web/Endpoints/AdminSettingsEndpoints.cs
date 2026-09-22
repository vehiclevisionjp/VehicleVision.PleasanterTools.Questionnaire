using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>特権管理者がアプリケーション設定を読み書きする入口。</summary>
public static class AdminSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAdminSettingsEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/settings")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.SettingsManage));
        group.AddEndpointFilter<AuditLogFilter>();

        group.MapGet("", async (
            IAppSettingsProvider provider,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(Body(snapshot));
        });

        group.MapPut("", async (
            AppSettingsRequest request,
            HttpContext context,
            IAppSettingsProvider provider,
            CancellationToken cancellationToken) =>
        {
            if ((request.AdminNotice?.Length ?? 0) > 1000)
            {
                return Results.BadRequest(new { message = "管理者向けのお知らせは 1000 文字以内で入力してください。" });
            }

            var before = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!before.FixedKeys.Contains(AppSettingsProvider.AdminNoticeKey)
                && !string.Equals(
                    before[AppSettingsProvider.AdminNoticeKey],
                    request.AdminNotice?.Trim() ?? string.Empty,
                    StringComparison.Ordinal))
            {
                AuditNotes.SetTarget(context, "AppSetting", AppSettingsProvider.AdminNoticeKey);
                AuditNotes.Add(context, "changedKeys", AppSettingsProvider.AdminNoticeKey);
            }

            var adminUserId = Guid.Parse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var snapshot = await provider.SaveAsync(
                new Dictionary<string, string?>
                {
                    [AppSettingsProvider.AdminNoticeKey] = request.AdminNotice,
                },
                adminUserId,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(Body(snapshot));
        });

        return builder;
    }

    private static object Body(AppSettingsSnapshot snapshot) => new
    {
        adminNotice = snapshot[AppSettingsProvider.AdminNoticeKey],
        fixedFields = new
        {
            adminNotice = snapshot.FixedKeys.Contains(AppSettingsProvider.AdminNoticeKey),
        },
    };

    public sealed record AppSettingsRequest(string? AdminNotice);
}
