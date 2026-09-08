using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理者の管理の入口。</summary>
/// <remarks>
/// <para>
/// **他人に触れるのは <see cref="AdminRole.Administrator"/> だけ。**
/// <see cref="AdminRole.Editor"/> にできるのは、自分のパスワードと自分の 2 要素だけ
/// （<c>_documents/非機能設計.md</c> 1 章）。
/// </para>
/// <para>
/// **誰も入れなくなる操作を通さない。** 最後の管理者を止めること・降格させること、
/// 自分を止めること・自分の役割を変えることは、いずれも拒む。
/// </para>
/// <para>
/// **招待だけは認証を通っていない相手が叩く**（<c>/api/admin/invitations/accept</c>）。
/// 既定のパスワードを配らないための道なので、
/// **期限付き・1 回限り**で、レート制限もログインと同じ枠に入れる。
/// </para>
/// </remarks>
public static class AdminUserEndpoints
{
    /// <summary>登録し直しの途中に預ける共有鍵の claim。</summary>
    private const string ReenrollSecretClaim = "questionnaire:totp_reenrollment_secret";

    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin");
        AdminAuthSchemes.AddNoStore(group);

        // **管理操作を残す**（Issue #19）。読み取りは残さない
        group.AddEndpointFilter<AuditLogFilter>();

        MapUsers(group);
        MapSelf(group);
        MapInvitationAcceptance(group);

        return builder;
    }

    // ---- 他人を触る（Administrator だけ） ------------------------------------
    private static void MapUsers(RouteGroupBuilder parent)
    {
        var users = parent.MapGroup("/users")
            .RequireAuthorization(AdminAuthSchemes.AdministratorPolicy);

        // ---- 一覧 ------------------------------------------------------------
        users.MapGet("", async (AdminUserService service, CancellationToken cancellationToken) =>
        {
            var list = await service.ListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                users = list.Select(user => new
                {
                    adminUserId = user.AdminUserId,
                    loginId = user.LoginId,
                    role = user.Role.ToString(),
                    isDisabled = user.IsDisabled,
                    hasTotp = user.HasTotp,
                    // まだ招待を受け取っていない（＝一度も入っていない）
                    invitationPending = user.InvitationPending,
                    // **止め忘れを見つける唯一の手掛かり**
                    lastLoginAt = AsUtc(user.LastLoginAt),
                    createdAt = AsUtc(user.CreatedAt),
                }),
            });
        });

        // ---- 追加（招待を出す） ----------------------------------------------
        users.MapPost("", async (
            AdminUserCreateRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
        {
            var language = RequestLanguage.Of(context);

            // **誰を何にしようとしたかまで残す。** 断られた試みも記録に残る
            AuditNotes.Add(context, "loginId", request.LoginId);
            AuditNotes.Add(context, "role", request.Role);

            if (ParseRole(request.Role) is not { } role)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.RoleMustBeEditorOrAdministrator, language),
                });
            }

            var (outcome, invitation) = await service
                .InviteAsync(ActorId(principal), request.LoginId, role, cancellationToken)
                .ConfigureAwait(false);

            return outcome is AdminUserOutcome.Succeeded
                ? Results.Ok(InvitationBody(invitation!))
                : Failure(outcome, language);
        });

        // ---- 招待の出し直し --------------------------------------------------
        users.MapPost("/{adminUserId:guid}/invitation", async (
            Guid adminUserId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
        {
            var (outcome, invitation) = await service
                .ReissueInvitationAsync(ActorId(principal), adminUserId, cancellationToken)
                .ConfigureAwait(false);

            return outcome is AdminUserOutcome.Succeeded
                ? Results.Ok(InvitationBody(invitation!))
                : Failure(outcome, RequestLanguage.Of(context));
        });

        // ---- 無効化・有効化 --------------------------------------------------
        users.MapPost("/{adminUserId:guid}/disable", (
            Guid adminUserId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
            SetDisabledAsync(
                adminUserId, context, principal, service, isDisabled: true, cancellationToken));

        users.MapPost("/{adminUserId:guid}/enable", (
            Guid adminUserId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
            SetDisabledAsync(
                adminUserId, context, principal, service, isDisabled: false, cancellationToken));

        // ---- 役割の変更 ------------------------------------------------------
        users.MapPost("/{adminUserId:guid}/role", async (
            Guid adminUserId,
            AdminRoleRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
        {
            var language = RequestLanguage.Of(context);

            // **何の役割にしようとしたかまで残す。** 断られた試みも記録に残る
            AuditNotes.Add(context, "role", request.Role);

            if (ParseRole(request.Role) is not { } role)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.RoleMustBeEditorOrAdministrator, language),
                });
            }

            var outcome = await service
                .SetRoleAsync(ActorId(principal), adminUserId, role, cancellationToken)
                .ConfigureAwait(false);

            return outcome is AdminUserOutcome.Succeeded
                ? Results.Ok(new { role = role.ToString() })
                : Failure(outcome, language);
        });
    }

    // ---- 自分を触る（役割を問わない） ----------------------------------------
    private static void MapSelf(RouteGroupBuilder parent)
    {
        var me = parent.MapGroup("/me").RequireAuthorization(AdminAuthSchemes.SessionPolicy);

        // ---- パスワードの変更 ----------------------------------------------------
        me.MapPost("/password", async (
            AdminPasswordChangeRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            CancellationToken cancellationToken) =>
        {
            var outcome = await service.ChangeOwnPasswordAsync(
                ActorId(principal),
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken).ConfigureAwait(false);

            return outcome is AdminUserOutcome.Succeeded
                ? Results.Ok(new { changed = true })
                : Failure(outcome, RequestLanguage.Of(context));
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- 表示言語 --------------------------------------------------------
        // **自分の設定なので役割を問わない。** 他人の言語は変えられない
        me.MapPut("/language", async (
            AdminLanguageRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            IAdminUserStore store,
            CancellationToken cancellationToken) =>
        {
            // **空なら「選んでいない」へ戻す。** ブラウザの言語設定に従うようになる
            var language = string.IsNullOrWhiteSpace(request.Language)
                ? null
                : SupportedLanguages.Normalize(request.Language);

            if (!string.IsNullOrWhiteSpace(request.Language) && language is null)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.UnsupportedLanguage, RequestLanguage.Of(context)),
                });
            }

            await store.SetLanguageAsync(ActorId(principal), language, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { language });
        });

        // ---- 2 要素の登録し直し（端末を替えたとき） --------------------------
        me.MapPost("/totp/begin", async (
            AdminPasswordRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminUserService service,
            AdminAuthenticator authenticator,
            CancellationToken cancellationToken) =>
        {
            var actorId = ActorId(principal);

            // **パスワードをもう一度求める。** 画面を離席で奪われただけで
            // 2 要素を差し替えられては、乗っ取りが完成してしまう
            var outcome = await service
                .ConfirmOwnPasswordAsync(actorId, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not AdminUserOutcome.Succeeded)
            {
                return Failure(outcome, RequestLanguage.Of(context));
            }

            var loginId = principal.Identity?.Name ?? string.Empty;
            var enrollment = authenticator.BeginTotpEnrollment(loginId);

            // **共有鍵は cookie 側に預ける。** 画面から送り返させると差し替えられる
            await SignInReenrollAsync(context, actorId, loginId, enrollment.SecretBase32)
                .ConfigureAwait(false);

            return Results.Ok(new { secret = enrollment.SecretBase32, uri = enrollment.OtpAuthUri });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        me.MapPost("/totp/complete", async (
            AdminCodeRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminAuthenticator authenticator,
            CancellationToken cancellationToken) =>
        {
            var actorId = ActorId(principal);
            var reenroll = await context.AuthenticateAsync(AdminAuthSchemes.Reenroll).ConfigureAwait(false);

            // **同じ人の途中でなければ通さない**
            if (!reenroll.Succeeded
                || !Guid.TryParse(
                    reenroll.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var pendingId)
                || pendingId != actorId
                || reenroll.Principal?.FindFirstValue(ReenrollSecretClaim) is not { Length: > 0 } secret)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.EnrollmentRestartRequired, RequestLanguage.Of(context)),
                });
            }

            var codes = await authenticator
                .CompleteTotpEnrollmentAsync(actorId, secret, request.Code ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (codes is null)
            {
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.TotpCodeMismatch, RequestLanguage.Of(context)),
                    },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // **共有鍵の claim を残さない**
            await context.SignOutAsync(AdminAuthSchemes.Reenroll).ConfigureAwait(false);

            // **前の復旧コードは使えなくなる。** 返せるのはここだけ
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);
    }

    // ---- 招待を受け取る（認証を通っていない相手） ----------------------------
    private static void MapInvitationAcceptance(RouteGroupBuilder parent) =>
        parent.MapPost("/invitations/accept", async (
            AdminInvitationAcceptRequest request,
            HttpContext context,
            AdminUserService service,
            CancellationToken cancellationToken) =>
        {
            var (outcome, user) = await service
                .AcceptInvitationAsync(request.Token, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not AdminUserOutcome.Succeeded)
            {
                return Failure(outcome, RequestLanguage.Of(context));
            }

            // **パスワードを決めただけでは入れない。** 2 要素まで通って初めてログインとする
            await AdminAuthEndpoints.SignInPendingAsync(context, user!, secret: null).ConfigureAwait(false);
            return Results.Ok(new { next = user!.HasTotp ? "totp" : "enroll" });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

    private static async Task<IResult> SetDisabledAsync(
        Guid adminUserId,
        HttpContext context,
        ClaimsPrincipal principal,
        AdminUserService service,
        bool isDisabled,
        CancellationToken cancellationToken)
    {
        var outcome = await service
            .SetDisabledAsync(ActorId(principal), adminUserId, isDisabled, cancellationToken)
            .ConfigureAwait(false);

        return outcome is AdminUserOutcome.Succeeded
            ? Results.Ok(new { isDisabled })
            : Failure(outcome, RequestLanguage.Of(context));
    }

    /// <summary>結果を応答へ写す。</summary>
    /// <remarks>
    /// **招待だけは理由を分けない。** 無い・期限切れ・使用済みを区別して返すと、
    /// トークンの当たり外れを外から確かめられる。
    /// </remarks>
    private static IResult Failure(AdminUserOutcome outcome, string language)
    {
        string Message(string key) => ServerMessages.Get(key, language);

        return outcome switch
        {
            AdminUserOutcome.NotFound =>
                Results.NotFound(new { message = Message(ServerMessageKeys.AdminUserNotFound) }),

            AdminUserOutcome.DuplicateLoginId =>
                Results.Conflict(new { message = Message(ServerMessageKeys.DuplicateLoginId) }),

            AdminUserOutcome.InvalidInput =>
                Results.BadRequest(new { message = Message(ServerMessageKeys.InvalidInput) }),

            AdminUserOutcome.WeakPassword =>
                Results.BadRequest(new { message = AdminPasswordPolicy.Message(language) }),

            AdminUserOutcome.SelfNotAllowed =>
                Results.Conflict(new { message = Message(ServerMessageKeys.SelfNotAllowed) }),

            AdminUserOutcome.LastAdministrator =>
                Results.Conflict(new { message = Message(ServerMessageKeys.LastAdministrator) }),

            AdminUserOutcome.LockedOut =>
                Results.Json(
                    new { message = Message(ServerMessageKeys.OperationTemporarilyLocked) },
                    statusCode: StatusCodes.Status423Locked),

            AdminUserOutcome.InvitationInvalid =>
                Results.Json(
                    new { message = Message(ServerMessageKeys.InvitationInvalid) },
                    statusCode: StatusCodes.Status401Unauthorized),

            _ => Results.Json(
                new { message = Message(ServerMessageKeys.CurrentPasswordRejected) },
                statusCode: StatusCodes.Status401Unauthorized),
        };
    }

    private static object InvitationBody(IssuedInvitation invitation) => new
    {
        adminUserId = invitation.AdminUserId,
        // **返せるのはこの時だけ。** 保存しているのはハッシュのみ
        invitationToken = invitation.Token,
        expiresAt = AsUtc(invitation.ExpiresAt),
    };

    /// <summary>DB の時刻を UTC と分かる形にする。</summary>
    /// <remarks>
    /// 列は時間帯を持たないので、読み出した値の種別は <c>Unspecified</c> になる
    /// （<c>_documents/データモデル設計.md</c> 4 章）。
    /// **そのまま返すと、受け取った側が地方時と読み違える。**
    /// </remarks>
    private static DateTime? AsUtc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>役割を読む。**数字や未知の名前は受け付けない。**</summary>
    private static AdminRole? ParseRole(string? value) => value switch
    {
        nameof(AdminRole.Editor) => AdminRole.Editor,
        nameof(AdminRole.Administrator) => AdminRole.Administrator,
        _ => null,
    };

    private static Guid ActorId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static Task SignInReenrollAsync(
        HttpContext context,
        Guid adminUserId,
        string loginId,
        string secret)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, adminUserId.ToString()),
                new Claim(ClaimTypes.Name, loginId),
                new Claim(ReenrollSecretClaim, secret),
            ],
            AdminAuthSchemes.Reenroll);

        return context.SignInAsync(AdminAuthSchemes.Reenroll, new ClaimsPrincipal(identity));
    }

    /// <summary>追加する管理者。**パスワードは受け取らない**（招待で本人が決める）。</summary>
    public sealed record AdminUserCreateRequest(string? LoginId, string? Role);

    /// <summary>変更後の役割。</summary>
    public sealed record AdminRoleRequest(string? Role);

    /// <summary>今のパスワードと、新しいパスワード。</summary>
    public sealed record AdminPasswordChangeRequest(string? CurrentPassword, string? NewPassword);

    /// <summary>今のパスワード。</summary>
    public sealed record AdminPasswordRequest(string? Password);

    /// <summary>管理画面を出す言語。**空なら「選んでいない」に戻す。**</summary>
    public sealed record AdminLanguageRequest(string? Language);

    /// <summary>使い捨てパスワード。</summary>
    public sealed record AdminCodeRequest(string? Code);

    /// <summary>招待と、本人が決めたパスワード。</summary>
    public sealed record AdminInvitationAcceptRequest(string? Token, string? Password);
}
