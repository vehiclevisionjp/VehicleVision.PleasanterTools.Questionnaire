using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>ログイン中の端末を一覧・失効する入口。</summary>
public static class AdminSessionEndpoints
{
    public static IEndpointRouteBuilder MapAdminSessionEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin");
        AdminAuthSchemes.AddNoStore(group);
        group.AddEndpointFilter<AuditLogFilter>();

        var me = group.MapGroup("/me/sessions")
            .RequireAuthorization(AdminAuthSchemes.SessionPolicy);

        me.MapGet("", async (
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                sessions = await RowsAsync(
                    store, ActorId(principal), CurrentSessionId(principal), time, cancellationToken)
                    .ConfigureAwait(false),
            }));

        me.MapPost("/revoke-others", async (
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            CancellationToken cancellationToken) =>
        {
            var revoked = await store.DeleteAllExceptAsync(
                ActorId(principal), CurrentSessionId(principal), cancellationToken)
                .ConfigureAwait(false);
            AuditNotes.SetTargets(context, "AdminSession", revoked);
            return Results.Ok(new { revoked = revoked.Count });
        });

        me.MapPost("/{adminSessionId:guid}/revoke", async (
            Guid adminSessionId,
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            CancellationToken cancellationToken) =>
        {
            if (adminSessionId == CurrentSessionId(principal))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.CurrentSessionCannotBeRevoked,
                        RequestLanguage.Of(context)),
                });
            }

            return await RevokeAsync(
                adminSessionId,
                ActorId(principal),
                context,
                store,
                cancellationToken).ConfigureAwait(false);
        });

        var users = group.MapGroup("/users/{adminUserId:guid}/sessions")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.UsersRead));

        users.MapGet("", async (
            Guid adminUserId,
            IAdminSessionStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                sessions = await RowsAsync(
                    store, adminUserId, currentSessionId: null, time, cancellationToken)
                    .ConfigureAwait(false),
            }));

        users.MapPost("/{adminSessionId:guid}/revoke", async (
            Guid adminUserId,
            Guid adminSessionId,
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            CancellationToken cancellationToken) =>
        {
            if (adminUserId == ActorId(principal))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.SelfNotAllowed,
                        RequestLanguage.Of(context)),
                });
            }

            return await RevokeAsync(
                adminSessionId,
                adminUserId,
                context,
                store,
                cancellationToken).ConfigureAwait(false);
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.UsersWrite));

        return builder;
    }

    private static async Task<IResult> RevokeAsync(
        Guid adminSessionId,
        Guid expectedUserId,
        HttpContext context,
        IAdminSessionStore store,
        CancellationToken cancellationToken)
    {
        var session = await store.FindAsync(adminSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null
            || session.AdminUserId != expectedUserId
            || session.Kind is not AdminSessionKind.Session)
        {
            return Results.NotFound(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.AdminSessionNotFound,
                    RequestLanguage.Of(context)),
            });
        }

        AuditNotes.SetTarget(context, "AdminSession", adminSessionId.ToString());
        await store.DeleteAsync(adminSessionId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { revoked = true });
    }

    private static async Task<IReadOnlyList<object>> RowsAsync(
        IAdminSessionStore store,
        Guid adminUserId,
        Guid? currentSessionId,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var sessions = await store.ListAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => AsUtc(session.ExpiresAt) > now)
            .Select(session => (object)new
            {
                adminSessionId = session.AdminSessionId,
                current = session.AdminSessionId == currentSessionId,
                createdAt = AsUtc(session.CreatedAt),
                expiresAt = AsUtc(session.ExpiresAt),
                ipAddress = session.IpAddress,
                userAgent = session.UserAgent,
            })
            .ToList();
    }

    private static Guid ActorId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static Guid? CurrentSessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(AdminSessionManager.SessionIdClaim), out var id)
            ? id
            : null;

    private static DateTime AsUtc(DateTime value) =>
        value.Kind is DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
