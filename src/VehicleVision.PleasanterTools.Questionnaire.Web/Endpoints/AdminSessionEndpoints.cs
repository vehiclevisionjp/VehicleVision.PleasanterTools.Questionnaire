using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>ログイン中の端末を一覧・失効する入口。</summary>
/// <remarks>
/// <para>
/// 端末を指すのは、<b>セッション ID の SHA-256（64 文字の 16 進。<c>adminSessionId</c>）</b>。
/// cookie に載せている ID の現物ではないので、一覧に出してよい（そこから有効な cookie は作れない）。
/// </para>
/// </remarks>
public static class AdminSessionEndpoints
{
    /// <summary>端末の識別子の形（SHA-256 の小文字の 16 進 64 文字）。経路の条件に使う。</summary>
    private const string HandleRoute = "{adminSessionId:regex(^[0-9a-f]{{64}}$)}";

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
                    store, ActorId(principal), CurrentHandle(principal, store), time, cancellationToken)
                    .ConfigureAwait(false),
            }));

        me.MapPost("/revoke-others", async (
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            CancellationToken cancellationToken) =>
        {
            var revoked = await store.RevokeAllExceptAsync(
                ActorId(principal), principal.FindFirstValue(AdminSessionManager.SessionIdClaim), cancellationToken)
                .ConfigureAwait(false);
            AuditNotes.SetTargets(context, "AdminSession", revoked);
            return Results.Ok(new { revoked = revoked.Count });
        });

        me.MapPost("/" + HandleRoute + "/revoke", async (
            string adminSessionId,
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminSessionStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.Equals(adminSessionId, CurrentHandle(principal, store), StringComparison.Ordinal))
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
                    store, adminUserId, currentHandle: null, time, cancellationToken)
                    .ConfigureAwait(false),
            }));

        users.MapPost("/" + HandleRoute + "/revoke", async (
            Guid adminUserId,
            string adminSessionId,
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
        string adminSessionId,
        Guid expectedUserId,
        HttpContext context,
        IAdminSessionStore store,
        CancellationToken cancellationToken)
    {
        // **持ち主の組で消す。** 他人の端末の識別子を渡されても消えず、「無い」と同じ応答になる
        if (!await store.DeleteByHandleAsync(expectedUserId, adminSessionId, cancellationToken).ConfigureAwait(false))
        {
            return Results.NotFound(new
            {
                message = ServerMessages.Get(
                    ServerMessageKeys.AdminSessionNotFound,
                    RequestLanguage.Of(context)),
            });
        }

        AuditNotes.SetTarget(context, "AdminSession", adminSessionId);
        return Results.Ok(new { revoked = true });
    }

    private static async Task<IReadOnlyList<object>> RowsAsync(
        IAdminSessionStore store,
        Guid adminUserId,
        string? currentHandle,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sessions = await store.ListForUserAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        return sessions
            .Where(session => session.ExpiresAt > now)
            .Select(session => (object)new
            {
                adminSessionId = session.Handle,
                current = string.Equals(session.Handle, currentHandle, StringComparison.Ordinal),
                createdAt = session.CreatedAt.UtcDateTime,
                expiresAt = session.ExpiresAt.UtcDateTime,
                ipAddress = session.IpAddress,
                userAgent = session.UserAgent,
                // 方式の名前だけ。**文言は画面が言語に合わせて作る**
                loginMethod = LoginMethodOf(session.Via),
            })
            .ToList();
    }

    /// <summary>セッションに入った方式（Core の <see cref="SessionVia"/>）を、画面が読む名前にする。</summary>
    private static string? LoginMethodOf(string via) => via switch
    {
        SessionVia.Password => "Password",
        SessionVia.Saml => "Saml",
        SessionVia.Pleasanter => "PleasanterSso",
        _ => null,
    };

    private static Guid ActorId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>いまのセッションを指す識別子（cookie の ID の SHA-256）。</summary>
    private static string? CurrentHandle(ClaimsPrincipal principal, IAdminSessionStore store) =>
        principal.FindFirstValue(AdminSessionManager.SessionIdClaim) is { Length: > 0 } sessionId
            ? store.HandleOf(sessionId)
            : null;
}
