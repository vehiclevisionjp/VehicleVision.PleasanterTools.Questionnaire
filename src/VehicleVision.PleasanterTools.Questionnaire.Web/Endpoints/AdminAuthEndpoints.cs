using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Mail;
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
    /// <summary>段階を問わず同じ文言を返す。</summary>
    private static string InvalidMessage(HttpContext context) =>
        ServerMessages.Get(ServerMessageKeys.InvalidCredentials, RequestLanguage.Of(context));

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin")
            .WithTags("管理 API");
        AdminAuthSchemes.AddNoStore(group);

        // **管理操作を残す**（Issue #19）。読み取りは残さない
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- 今の状態 --------------------------------------------------------
        group.MapGet("/session", async (
            HttpContext context,
            IAdminUserStore store,
            IAdminUserSettingsStore userSettings,
            ISamlOptionsProvider samlProvider,
            IPleasanterSsoOptionsProvider pleasanterSsoProvider,
            AdminPasswordSignInPolicy passwordSignIn,
            AdminAuthOptions options,
            BotMitigationOptionsProvider botOptionsProvider,
            IMailSettingsProvider mailSettings,
            CancellationToken cancellationToken) =>
        {
            var setupRequired = await store.CountAsync(cancellationToken).ConfigureAwait(false) == 0;
            var saml = (await samlProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            var pleasanterSso = (await pleasanterSsoProvider.GetAsync(cancellationToken).ConfigureAwait(false))
                .Options;
            var mail = await mailSettings.GetAsync(cancellationToken).ConfigureAwait(false);
            var botOptions = await botOptionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);

            // **SAML が使えるかは未認証の相手にも返す。** ログイン画面に釦を出すため。
            // ⚠️ **設定の中身は返さない**（証明書・EntityID は画面に要らない）
            var samlEnabled = saml.Enabled;
            var samlLabel = saml.ButtonLabel.Length > 0 ? saml.ButtonLabel : null;

            // **Pleasanter のログインも同じ扱い**（Issue #464）。
            // 返すのはブラウザで開くログイン画面の URL だけ。**内部 URL は返さない**
            var pleasanterSsoEnabled = pleasanterSso.Enabled;
            var pleasanterSsoLabel = pleasanterSso.ButtonLabel.Length > 0 ? pleasanterSso.ButtonLabel : null;
            var pleasanterSsoLoginUrl = pleasanterSsoEnabled ? pleasanterSso.LoginUrl : null;

            // **合言葉を塞げるのは、ほかの入口（SAML か Pleasanter）が有効な間だけ**
            var passwordSignInEnabled =
                setupRequired || passwordSignIn.IsAllowed(context, samlEnabled || pleasanterSsoEnabled);

            var session = await context.AuthenticateAsync(AdminAuthSchemes.Session).ConfigureAwait(false);
            if (session.Succeeded)
            {
                // **利用者ごとの言語は画面の初期値。** 未設定なら null を返し、
                // 画面はブラウザの言語設定へ落とす（_documents/多言語対応方針.md 2 章）
                string? language = null;
                var hasTotp = false;
                var responseNotificationEnabled = false;
                if (session.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) is { } sessionId
                    && Guid.TryParse(sessionId, out var sessionUserId))
                {
                    var user = await store.FindByIdAsync(sessionUserId, cancellationToken)
                        .ConfigureAwait(false);
                    language = SupportedLanguages.Normalize(user?.Language);
                    hasTotp = user?.TotpEnabled ?? false;
                    responseNotificationEnabled = await userSettings
                        .GetResponseNotificationEnabledAsync(sessionUserId, cancellationToken)
                        .ConfigureAwait(false);
                }

                var role = session.Principal?.FindFirstValue(ClaimTypes.Role);

                // **この人が SAML で入ったか**（Issue #191）。
                // **IdP へログアウトを頼めるのは、SAML で入った人だけ。**
                // ⚠️ **NameID そのものは返さない。** 画面には要らない値
                var samlSingleLogout = saml.SingleLogoutEnabled
                    && session.Principal?.FindFirstValue(AdminAuthSchemes.SamlNameIdClaim) is { Length: > 0 };

                // **Pleasanter のログインで入った人だけに、Pleasanter のログアウト画面を返す**（Issue #464）
                var viaPleasanterSso = session.Principal?.FindFirst(PleasanterSsoClaims.UserId) is not null;
                var pleasanterSsoLogoutUrl = pleasanterSsoEnabled
                    && viaPleasanterSso
                    && pleasanterSso.LogoutUrl.Length > 0
                        ? pleasanterSso.LogoutUrl
                        : null;

                return Results.Ok(new
                {
                    authenticated = true,
                    setupRequired,

                    // **自分の管理者 ID を返す**（Issue #156）。
                    // 画面が「自分自身への操作」を止めるために要る。
                    // ⚠️ **秘密ではない**（一覧にも同じ ID が出る）
                    adminUserId = session.Principal?.FindFirstValue(ClaimTypes.NameIdentifier),
                    loginId = session.Principal?.Identity?.Name,
                    role,
                    // **画面は権限で出し分ける**（Issue #160）。
                    // 「Administrator かどうか」で分けると、役割を増やすたびに画面を直すことになる
                    permissions = AdminPermissions.Of(role),
                    language,
                    // **画面で「登録する／解除する」を出し分けるために要る**（Issue #154）
                    twoFactor = options.TwoFactor.ToString(),
                    hasTotp,
                    samlEnabled,
                    samlLabel,
                    pleasanterSsoEnabled,
                    pleasanterSsoLabel,
                    pleasanterSsoLoginUrl,
                    passwordSignInEnabled,
                    captchaEnabled = botOptions.AdminCaptcha.Enabled,

                    // **IdP へログアウトを頼めるか**（Issue #191）。
                    // 画面はこれを見て、ログアウトの行き先を決める
                    samlSingleLogout,

                    // **Pleasanter のログインで入ったか**（Issue #464）。
                    // 画面はログアウトの直後に自動で入り直さないよう、これを見て印を付ける
                    viaPleasanterSso,
                    pleasanterSsoLogoutUrl,

                    // **メールを送れる状態かを画面へ返す**（Issue #189）。
                    // 自動返信を設定しただけで「送っているつもり」にさせない。
                    // ⚠️ **接続先も資格情報も返さない**（送れるか否かだけ）。
                    // **認証済みの相手にだけ返す**（構成の情報を未認証へ出さない）
                    mailEnabled = mail.IsReady,
                    responseNotificationEnabled,

                    // **試し送信の宛先は自分のログイン ID に固定する**（Issue #319）。
                    // 画面で先に理由を出すため、メールアドレスとして読めるかだけ返す。
                    autoReplyTestRecipientAvailable = MailAddress.TryCreate(
                        session.Principal?.Identity?.Name,
                        out _),
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
                needsEnrollment = user is not null && !user.TotpEnabled;
            }

            return Results.Ok(new
            {
                authenticated = false,
                setupRequired,
                // **途中状態かどうかは画面の出し分けに要る**
                pending = pending.Succeeded,
                pendingLoginId = pending.Succeeded ? pending.Principal?.Identity?.Name : null,
                needsEnrollment,
                samlEnabled,
                samlLabel,
                pleasanterSsoEnabled,
                pleasanterSsoLabel,
                pleasanterSsoLoginUrl,
                passwordSignInEnabled,
                captchaEnabled = botOptions.AdminCaptcha.Enabled,
            });
        });

        // **課題を出すだけでは利用者を調べない。** ログイン ID の実在を応答時間へ出さない。
        group.MapGet("/captcha/challenge", async (
            BotMitigationOptionsProvider botOptionsProvider,
            AltchaGuard altcha,
            CancellationToken cancellationToken) =>
        {
            var botOptions = await botOptionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            return botOptions.AdminCaptcha.Enabled
                ? Results.Ok(altcha.Issue(botOptions.Altcha))
                : Results.NotFound();
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
                    .RecordSignInAsync(created.Id, cancellationToken)
                    .ConfigureAwait(false);
                await SignInSessionAsync(context, created.Id).ConfigureAwait(false);
                return Results.Ok(new { next = "done" });
            }

            await SignInPendingAsync(context, created.Id).ConfigureAwait(false);
            return Results.Ok(new { next = "enroll" });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- パスワード ----------------------------------------------------------
        group.MapPost("/login", async (
            AdminCredentialRequest request,
            HttpContext context,
            AdminAuthenticator authenticator,
            BotMitigationOptionsProvider botOptionsProvider,
            AltchaGuard altcha,
            CancellationToken cancellationToken) =>
        {
            // **誰が狙われているかは、記録に残っていないと分からない。**
            // パスワードは預けない（AuditNotes の但し書き）
            AuditNotes.Add(context, "loginId", request.LoginId);

            // **この handler より先にレート制限 middleware が動く。**
            // 無制限に署名検証だけをさせて、サーバの CPU を使わせない。
            var botOptions = await botOptionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
            if (botOptions.AdminCaptcha.Enabled
                && await altcha.CheckRequiredAsync(request.Altcha, botOptions.Altcha, cancellationToken)
                    .ConfigureAwait(false) is not null)
            {
                return Results.Json(
                    new { message = InvalidMessage(context) },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

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
                    await SignInPendingAsync(context, result.User!.Id).ConfigureAwait(false);
                    return Results.Ok(new { next = "totp" });

                case PasswordOutcome.NeedsTotpEnrollment:
                    await SignInPendingAsync(context, result.User!.Id).ConfigureAwait(false);
                    return Results.Ok(new { next = "enroll" });

                case PasswordOutcome.SignedIn:
                    // **2 要素を求めない設定で、未登録の相手。** そのまま入れる（Issue #154）
                    await authenticator
                        .RecordSignInAsync(result.User!.Id, cancellationToken)
                        .ConfigureAwait(false);
                    await SignInSessionAsync(context, result.User!.Id).ConfigureAwait(false);
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
        })
            // **画面から隠すだけでは足りない。** API を直接叩かれても認証しない。
            .AddEndpointFilter<AdminPasswordSignInFilter>()
            .RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- 2 要素の登録 ----------------------------------------------------
        group.MapPost("/enroll/begin", async (
            HttpContext context,
            AdminAuthenticator authenticator,
            AdminAuthOptions options,
            CancellationToken cancellationToken) =>
        {
            var pending = await context.AuthenticateAsync(AdminAuthSchemes.Pending).ConfigureAwait(false);
            if (!pending.Succeeded
                || pending.Principal?.Identity?.Name is not { } loginId
                || !Guid.TryParse(pending.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
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

            // **共有鍵はサーバ側に持たせる**（「登録の途中の鍵」として、暗号化して DB に置く）。
            // 画面から送り返させると差し替えられる
            var enrollment = await authenticator
                .BeginTotpEnrollmentAsync(userId, loginId, cancellationToken)
                .ConfigureAwait(false);

            // **途中状態を張り直す**（登録の手順を終えるまでの時間を、ここから数え直す）。
            // **Pleasanter・SAML のログインで来た印も引き継ぐ**（Issue #464）。落とすと再検証から外れる
            await SignInPendingAsync(context, userId, SsoOf(pending.Principal)).ConfigureAwait(false);

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

            var user = ReadPending(pending.Principal);

            // **登録の途中の鍵はサーバ側にある。** 無ければ（始めていない・壊れている）、やり直させる
            var secret = await authenticator
                .GetPendingEnrollmentSecretAsync(user.UserId, cancellationToken)
                .ConfigureAwait(false);
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

            var codes = await authenticator
                .CompleteTotpEnrollmentAsync(user.UserId, secret, request.Code ?? string.Empty, cancellationToken)
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

            await SignInSessionAsync(context, user.UserId, SsoOf(pending.Principal)).ConfigureAwait(false);

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
                (user, token) => authenticator.VerifyTotpAsync(user.UserId, request.Code ?? string.Empty, token)))
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
                    user.UserId, request.Code ?? string.Empty, token)))
            .RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- ログアウト ------------------------------------------------------
        group.MapPost("/logout", async (HttpContext context) =>
        {
            var sessions = context.RequestServices.GetRequiredService<AdminSessionManager>();

            // **途中状態も一緒に消す。** 残しておくと 2 要素から再開できてしまう
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Session).ConfigureAwait(false);
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Pending).ConfigureAwait(false);

            // 2 要素の登録し直しの途中も消す
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Reenroll).ConfigureAwait(false);
            return Results.Ok(new { signedOut = true });
        });

        return builder;
    }

    private static async Task<IResult> CompleteSecondFactorAsync(
        HttpContext context,
        CancellationToken cancellationToken,
        Func<PendingUser, CancellationToken, Task<SecondFactorOutcome>> verify)
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
                // **Pleasanter・SAML のログインで来た印を引き継ぐ**（Issue #464）
                await SignInSessionAsync(context, user.UserId, SsoOf(pending.Principal)).ConfigureAwait(false);
                return Results.Ok(new { authenticated = true });

            case SecondFactorOutcome.LockedOut:
                await context.RequestServices.GetRequiredService<AdminSessionManager>()
                    .RevokeCurrentAsync(context, AdminAuthSchemes.Pending).ConfigureAwait(false);
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

    /// <summary>途中状態の持ち主。**照合に要る値だけ**（パスワードのハッシュなどは持たない）。</summary>
    private readonly record struct PendingUser(Guid UserId);

    /// <summary>途中状態の claim から持ち主を読む。</summary>
    private static PendingUser ReadPending(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!));

    /// <summary>
    /// 要求の principal（ストアの値から組み立てたもの）から、SAML・Pleasanter のログインで来た印を取り出す。
    /// 2 要素の途中状態からログイン後へ<b>そのまま引き継ぐ</b>ために使う。無ければ <c>null</c>。
    /// </summary>
    internal static SessionSso? SsoOf(ClaimsPrincipal? principal)
    {
        var nameId = principal?.FindFirstValue(AdminAuthSchemes.SamlNameIdClaim);
        var saml = string.IsNullOrEmpty(nameId)
            ? null
            : new SamlSessionKeys(nameId, principal!.FindFirstValue(AdminAuthSchemes.SamlSessionIndexClaim));
        var pleasanter = PleasanterSsoClaims.KeysOf(principal);

        return saml is null && pleasanter is null ? null : new SessionSso(saml, pleasanter);
    }

    /// <summary>パスワードまで通った状態にする。**ここでは何も操作させない。**</summary>
    /// <param name="sso">
    /// ログイン後へ引き継ぐ、SAML・Pleasanter のログインで来た印（Issue #464・#191）。
    /// **ログインに使った方式は、この印から決まる**（端末の一覧に出す。Issue #602）。無ければパスワード。
    /// </param>
    internal static Task SignInPendingAsync(HttpContext context, Guid userId, SessionSso? sso = null) =>
        context.RequestServices.GetRequiredService<AdminSessionManager>().SignInAsync(
            context,
            AdminAuthSchemes.Pending,
            AdminSessionKinds.Pending,
            userId,
            AdminAuthSchemes.PendingLifetime,
            sso);

    /// <summary>2 要素まで通った状態にする。</summary>
    /// <remarks>
    /// **招待の受け取り**（<c>AdminUserEndpoints</c>。Issue #169）と
    /// **SAML の受け口**（<c>AdminSamlEndpoints</c>。Issue #166）からも同じ形で入る。
    /// </remarks>
    /// <param name="sso">
    /// SAML で入ったときの <c>NameID</c> と <c>SessionIndex</c>（Issue #191。**単一ログアウトの要求に載せるのに要る**）、
    /// Pleasanter のログインで入ったときの本人と確かめた時刻（Issue #464。再検証に使う）。
    /// それ以外の入り方では <c>null</c>。
    /// ⚠️ **SAML のアサーション全体は残さない。** 要らないものを持たない。
    /// </param>
    internal static async Task SignInSessionAsync(HttpContext context, Guid userId, SessionSso? sso = null)
    {
        var sessions = context.RequestServices.GetRequiredService<AdminSessionManager>();

        // **途中状態は必ず消す。** ストアにも途中の状態を残さない
        await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Pending).ConfigureAwait(false);

        await sessions.SignInAsync(
            context,
            AdminAuthSchemes.Session,
            AdminSessionKinds.Session,
            userId,
            AdminAuthSchemes.SessionLifetime,
            sso).ConfigureAwait(false);
    }

    /// <summary>ログイン ID とパスワード。</summary>
    public sealed record AdminCredentialRequest(
        string? LoginId,
        string? Password,
        string? Altcha = null);

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

    /// <summary>SAML で入ったときの <c>NameID</c>（Issue #191）。</summary>
    /// <remarks>
    /// **単一ログアウトの要求に載せる。** IdP 側はこれで「誰を落とすか」を決める。
    /// ⚠️ **ログイン ID と同じとは限らない**（属性から取る設定があるため）。
    /// </remarks>
    public const string SamlNameIdClaim = "q.saml.nameid";

    /// <summary>SAML で入ったときの <c>SessionIndex</c>（Issue #191）。</summary>
    /// <remarks>
    /// **IdP 側の「どのログイン」かを指す。** 同じ人が複数の端末で入っているとき、
    /// **この端末のぶんだけを落とす**のに要る。
    /// </remarks>
    public const string SamlSessionIndexClaim = "q.saml.sessionindex";

    /// <summary>ログインの試行に掛けるレート制限の名前。</summary>
    public const string LoginRateLimitPolicy = "admin-login";

    /// <summary>救済トークンの照合に掛ける専用レート制限の名前。</summary>
    public const string RescueRateLimitPolicy = "admin-rescue";

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
            // **Pleasanter に確かめ直せずに落としたときは 503**（Issue #464）。
            // 「ログアウトされた」と「Pleasanter に届かない」を画面で分けられるようにする
            context.Response.StatusCode =
                context.HttpContext.Items[PleasanterSsoSessionRevalidator.UpstreamErrorItemKey] is true
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
