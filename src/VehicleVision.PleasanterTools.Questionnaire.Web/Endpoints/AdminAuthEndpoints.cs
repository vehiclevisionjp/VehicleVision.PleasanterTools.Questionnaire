using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理画面の認証の入口。</summary>
/// <remarks>
/// <para>
/// **2 段階で通す。** パスワードが通った時点では
/// <see cref="AdminAuthSchemes.Pending"/> の途中状態にしかならず、
/// 使い捨てパスワードか復旧コードが通って初めて
/// <see cref="AdminAuthSchemes.Session"/> になる。
/// </para>
/// <para>
/// **外へ返す文言で段階を区別しない。** 「その利用者は居ない」と
/// 「パスワードが違う」を区別すると、利用者名の総当たりに使える。
/// </para>
/// </remarks>
public static class AdminAuthEndpoints
{
    /// <summary>途中状態に入れておく共有鍵の claim。</summary>
    private const string EnrollmentSecretClaim = "questionnaire:totp_enrollment_secret";

    /// <summary>段階を問わず同じ文言を返す。</summary>
    private static string InvalidMessage(HttpContext context) =>
        ServerMessages.Get(ServerMessageKeys.InvalidCredentials, RequestLanguage.Of(context));

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin");
        AdminAuthSchemes.AddNoStore(group);

        // **管理操作を残す**（Issue #19）。読み取りは残さない
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- 今の状態 --------------------------------------------------------
        group.MapGet("/session", async (
            HttpContext context,
            IAdminUserStore store,
            AdminAuthOptions options,
            CancellationToken cancellationToken) =>
        {
            var setupRequired = await store.IsEmptyAsync(cancellationToken).ConfigureAwait(false);

            var session = await context.AuthenticateAsync(AdminAuthSchemes.Session).ConfigureAwait(false);
            if (session.Succeeded)
            {
                // **利用者ごとの言語は画面の初期値。** 未設定なら null を返し、
                // 画面はブラウザの言語設定へ落とす（_documents/多言語対応方針.md 2 章）
                string? language = null;
                var hasTotp = false;
                if (session.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is { } sessionId
                    && Guid.TryParse(sessionId, out var sessionUserId))
                {
                    var user = await store.FindByIdAsync(sessionUserId, cancellationToken)
                        .ConfigureAwait(false);
                    language = SupportedLanguages.Normalize(user?.Language);
                    hasTotp = user?.HasTotp ?? false;
                }

                return Results.Ok(new
                {
                    authenticated = true,
                    setupRequired,
                    loginId = session.Principal?.Identity?.Name,
                    role = session.Principal?.FindFirstValue(ClaimTypes.Role),
                    language,
                    // **画面で「登録する／解除する」を出し分けるために要る**（Issue #154）
                    twoFactor = options.TwoFactor.ToString(),
                    hasTotp,
                });
            }

            var pending = await context.AuthenticateAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);

            // **途中状態の相手にだけ、2 要素の登録が要るかを返す。**
            // 未認証の相手へ返すと、その利用者が 2 要素を登録済みかどうかが漏れる
            var needsEnrollment = false;
            if (pending.Succeeded
                && pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is { } id
                && Guid.TryParse(id, out var pendingUserId))
            {
                var user = await store.FindByIdAsync(pendingUserId, cancellationToken)
                    .ConfigureAwait(false);
                needsEnrollment = user is not null && !user.HasTotp;
            }

            return Results.Ok(new
            {
                authenticated = false,
                setupRequired,
                // **途中状態かどうかは画面の出し分けに要る**
                pending = pending.Succeeded,
                pendingLoginId = pending.Succeeded ? pending.Principal?.Identity?.Name : null,
                needsEnrollment,
            });
        });

        // ---- 最初の管理者 ----------------------------------------------------
        group.MapPost("/setup", async (
            AdminCredentialRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            AdminPasswordPolicy policy,
            AdminAuthOptions options,
            CancellationToken cancellationToken) =>
        {
            // **誰が狙われているかは、記録に残っていないと分からない。**
            // パスワードは預けない（AuditNotes の但し書き）
            AuditNotes.Add(context, "loginId", request.LoginId);

            if (string.IsNullOrWhiteSpace(request.LoginId) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.LoginIdAndPasswordRequired, RequestLanguage.Of(context)),
                });
            }

            // **弱いパスワードを通さない。** 最初の 1 人こそ全権を持つ。
            // **ログイン ID と同じ値も、設定しだいで断る**（Issue #157）
            var loginId = request.LoginId.Trim();
            if (policy.Check(request.Password, loginId, RequestLanguage.Of(context))
                is { } passwordProblem)
            {
                return Results.BadRequest(new { message = passwordProblem });
            }

            var created = await authenticator
                .TryCreateFirstAdministratorAsync(loginId, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (created is null)
            {
                // **既に居るなら、ここは二度と使えない**
                return Results.Conflict(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.AdministratorAlreadyExists, RequestLanguage.Of(context)),
                });
            }

            // **2 要素が必須でなければ、登録を挟まずに入れる**（Issue #154）
            if (options.TwoFactor is not TwoFactorPolicy.Required)
            {
                await authenticator
                    .RecordSignInAsync(created.AdminUserId, cancellationToken)
                    .ConfigureAwait(false);
                await SignInSessionAsync(context, created).ConfigureAwait(false);
                return Results.Ok(new { next = "done" });
            }

            await SignInPendingAsync(context, created, secret: null).ConfigureAwait(false);
            return Results.Ok(new { next = "enroll" });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- パスワード ----------------------------------------------------------
        group.MapPost("/login", async (
            AdminCredentialRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            CancellationToken cancellationToken) =>
        {
            // **誰が狙われているかは、記録に残っていないと分からない。**
            // パスワードは預けない（AuditNotes の但し書き）
            AuditNotes.Add(context, "loginId", request.LoginId);

            if (string.IsNullOrWhiteSpace(request.LoginId) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Json(
                    new { message = InvalidMessage(context) },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await authenticator
                .CheckPasswordAsync(request.LoginId.Trim(), request.Password, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case PasswordOutcome.NeedsSecondFactor:
                    await SignInPendingAsync(context, result.User!, secret: null).ConfigureAwait(false);
                    return Results.Ok(new { next = "totp" });

                case PasswordOutcome.NeedsTotpEnrollment:
                    await SignInPendingAsync(context, result.User!, secret: null).ConfigureAwait(false);
                    return Results.Ok(new { next = "enroll" });

                case PasswordOutcome.SignedIn:
                    // **2 要素を求めない設定で、未登録の相手。** そのまま入れる（Issue #154）
                    await authenticator
                        .RecordSignInAsync(result.User!.AdminUserId, cancellationToken)
                        .ConfigureAwait(false);
                    await SignInSessionAsync(context, result.User!).ConfigureAwait(false);
                    return Results.Ok(new { next = "done" });

                case PasswordOutcome.LockedOut:
                    return Results.Json(
                        new
                        {
                            message = ServerMessages.Get(
                                ServerMessageKeys.LoginTemporarilyLocked, RequestLanguage.Of(context)),
                        },
                        statusCode: StatusCodes.Status423Locked);

                default:
                    // **止められている利用者も同じ文言にする**
                    return Results.Json(
                        new { message = InvalidMessage(context) },
                        statusCode: StatusCodes.Status401Unauthorized);
            }
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- 2 要素の登録 ----------------------------------------------------
        group.MapPost("/enroll/begin", async (
            HttpContext context,
            AdminAuthenticator authenticator,
            AdminAuthOptions options) =>
        {
            var pending = await context.AuthenticateAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);
            if (!pending.Succeeded || pending.Principal?.Identity?.Name is not { } loginId)
            {
                return Results.Unauthorized();
            }

            if (options.TwoFactor is TwoFactorPolicy.Disabled)
            {
                // **画面から隠すだけでは足りない。** API を直接叩かれても通さない
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.TwoFactorDisabled, RequestLanguage.Of(context)),
                });
            }

            var enrollment = authenticator.BeginTotpEnrollment(loginId);

            // **共有鍵は途中状態の側に持たせる。** 画面から送り返させると差し替えられる
            await SignInPendingAsync(context, ReadPending(pending.Principal), enrollment.SecretBase32)
                .ConfigureAwait(false);

            return Results.Ok(new { secret = enrollment.SecretBase32, uri = enrollment.OtpAuthUri });
        });

        group.MapPost("/enroll/complete", async (
            AdminCodeRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            AdminAuthOptions options,
            CancellationToken cancellationToken) =>
        {
            var pending = await context.AuthenticateAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);
            if (!pending.Succeeded || pending.Principal is null)
            {
                return Results.Unauthorized();
            }

            var secret = pending.Principal.FindFirstValue(EnrollmentSecretClaim);
            if (string.IsNullOrEmpty(secret))
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.EnrollmentRestartRequired, RequestLanguage.Of(context)),
                });
            }

            if (options.TwoFactor is TwoFactorPolicy.Disabled)
            {
                return Results.BadRequest(new
                {
                    message = ServerMessages.Get(
                        ServerMessageKeys.TwoFactorDisabled, RequestLanguage.Of(context)),
                });
            }

            var user = ReadPending(pending.Principal);
            var codes = await authenticator
                .CompleteTotpEnrollmentAsync(user.AdminUserId, secret, request.Code ?? string.Empty, cancellationToken)
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

            await SignInSessionAsync(context, user).ConfigureAwait(false);

            // **復旧コードを返せるのはここだけ。** 保存しているのはハッシュのみ
            return Results.Ok(new { recoveryCodes = codes });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- 2 要素 ----------------------------------------------------------
        group.MapPost("/login/totp", (
            AdminCodeRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            CancellationToken cancellationToken) =>
            CompleteSecondFactorAsync(
                context,
                cancellationToken,
                (user, token) => authenticator.VerifyTotpAsync(user.AdminUserId, request.Code ?? string.Empty, token)))
            .RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        group.MapPost("/login/recovery", (
            AdminCodeRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            CancellationToken cancellationToken) =>
            CompleteSecondFactorAsync(
                context,
                cancellationToken,
                (user, token) => authenticator.VerifyRecoveryCodeAsync(
                    user.AdminUserId, request.Code ?? string.Empty, token)))
            .RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- ログアウト ------------------------------------------------------
        group.MapPost("/logout", async (HttpContext context) =>
        {
            // **途中状態も一緒に消す。** 残しておくと 2 要素から再開できてしまう
            await context.SignOutAsync(AdminAuthSchemes.Session).ConfigureAwait(false);
            await context.SignOutAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);

            // 2 要素の登録し直しの途中も消す
            await context.SignOutAsync(AdminAuthSchemes.Reenroll).ConfigureAwait(false);
            return Results.Ok(new { signedOut = true });
        });

        return builder;
    }

    private static async Task<IResult> CompleteSecondFactorAsync(
        HttpContext context,
        CancellationToken cancellationToken,
        Func<AdminUser, CancellationToken, Task<SecondFactorOutcome>> verify)
    {
        var pending = await context.AuthenticateAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);
        if (!pending.Succeeded || pending.Principal is null)
        {
            return Results.Unauthorized();
        }

        var user = ReadPending(pending.Principal);
        var outcome = await verify(user, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case SecondFactorOutcome.Succeeded:
                await SignInSessionAsync(context, user).ConfigureAwait(false);
                return Results.Ok(new { authenticated = true });

            case SecondFactorOutcome.LockedOut:
                await context.SignOutAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.LoginTemporarilyLocked, RequestLanguage.Of(context)),
                    },
                    statusCode: StatusCodes.Status423Locked);

            case SecondFactorOutcome.NotEnrolled:
                return Results.Ok(new { next = "enroll" });

            default:
                return Results.Json(
                    new { message = InvalidMessage(context) },
                    statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>途中状態の claim から利用者を組み立てる。</summary>
    /// <remarks>
    /// **照合に要る値だけを持つ。** パスワードのハッシュなどは cookie に入れない。
    /// </remarks>
    private static AdminUser ReadPending(ClaimsPrincipal principal) => new()
    {
        AdminUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
        LoginId = principal.Identity?.Name ?? string.Empty,
        PasswordHash = string.Empty,
        Role = Enum.TryParse<AdminRole>(principal.FindFirstValue(ClaimTypes.Role), out var role)
            ? role
            : AdminRole.Editor,
    };

    /// <summary>パスワードまで通った状態にする。**ここでは何も操作させない。**</summary>
    internal static Task SignInPendingAsync(HttpContext context, AdminUser user, string? secret)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.AdminUserId.ToString()),
            new(ClaimTypes.Name, user.LoginId),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        if (secret is not null)
        {
            claims.Add(new Claim(EnrollmentSecretClaim, secret));
        }

        var identity = new ClaimsIdentity(claims, AdminAuthSchemes.Pending);
        return context.SignInAsync(AdminAuthSchemes.Pending, new ClaimsPrincipal(identity));
    }

    private static async Task SignInSessionAsync(HttpContext context, AdminUser user)
    {
        // **途中状態は必ず消す。** 共有鍵の claim を残さない
        await context.SignOutAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.AdminUserId.ToString()),
                new Claim(ClaimTypes.Name, user.LoginId),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ],
            AdminAuthSchemes.Session);

        await context.SignInAsync(
            AdminAuthSchemes.Session,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
    }

    /// <summary>ログイン ID とパスワード。</summary>
    public sealed record AdminCredentialRequest(string? LoginId, string? Password);

    /// <summary>使い捨てパスワードか復旧コード。</summary>
    public sealed record AdminCodeRequest(string? Code);
}

/// <summary>管理画面の認証で使う名前。</summary>
public static class AdminAuthSchemes
{
    /// <summary>2 要素まで通った状態。</summary>
    public const string Session = "Admin.Session";

    /// <summary>パスワードだけ通った途中の状態。**ここでは何も操作させない。**</summary>
    public const string Pending = "Admin.Pending";

    /// <summary>
    /// ログイン済みのまま 2 要素を登録し直している途中の状態。
    /// **共有鍵をここに預ける。**
    /// </summary>
    /// <remarks>
    /// <see cref="Pending"/> と分けているのは、
    /// **登録し直しの途中の cookie がログインの途中として使えないようにする**ため。
    /// </remarks>
    public const string Reenroll = "Admin.Reenroll";

    /// <summary>ログインの試行に掛けるレート制限の名前。</summary>
    public const string LoginRateLimitPolicy = "admin-login";

    /// <summary>ログイン済みなら通す認可の名前。</summary>
    public const string SessionPolicy = "Admin.Session.Any";

    /// <summary>
    /// <see cref="AdminRole.Administrator"/> だけ通す認可の名前。
    /// **他人に触れるのはこの役割だけ。**
    /// </summary>
    public const string AdministratorPolicy = "Admin.Session.Administrator";

    /// <summary>途中状態を保つ長さ。**短くする。**</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    /// <summary>2 要素を登録し直す途中を保つ長さ。**短くする。**</summary>
    public static readonly TimeSpan ReenrollLifetime = TimeSpan.FromMinutes(5);

    /// <summary>管理画面の応答を途中の経路に残さない。</summary>
    public static void AddNoStore(RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            return await next(context);
        });

    /// <summary>ログインを保つ長さ。</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    /// <summary>cookie の共通の設定を当てる。</summary>
    /// <remarks>
    /// <para>
    /// **画面側の JavaScript から読ませない**（<c>HttpOnly</c>）。
    /// **他所のサイトからの遷移で送らせない**（<c>SameSite=Strict</c>）。
    /// これで、別サイトに置かれた form からの操作が届かなくなる。
    /// </para>
    /// <para>
    /// **cookie の名前から中身を推させない。** 既定の名前は素性が知られている。
    /// </para>
    /// </remarks>
    public static void Configure(CookieAuthenticationOptions options, string name, TimeSpan lifetime)
    {
        options.Cookie.Name = name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = lifetime;
        options.SlidingExpiration = false;

        // **画面へ飛ばさない。** これは API なので、状態だけを返す
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
