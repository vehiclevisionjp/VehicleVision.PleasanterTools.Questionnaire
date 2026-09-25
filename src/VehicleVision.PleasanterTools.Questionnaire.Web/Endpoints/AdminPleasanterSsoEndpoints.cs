using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>Pleasanter のログインで管理画面へ入る入口（Issue #464）。</summary>
/// <remarks>
/// <para>
/// **流れ。** ログイン画面が <c>POST /api/admin/pleasanter-sso/check</c> を呼ぶ。
/// サーバはブラウザから届いた Pleasanter の cookie を Pleasanter の API へ転送し
/// （標準の <c>/api/users/get</c> を <c>Own</c> で絞る）、
/// 本人が返れば本アプリの管理者へ結び付けて本アプリの cookie を発行する。
/// Pleasanter にログインしていなければ、画面は Pleasanter のログイン画面を別窓で開き、
/// ログインが済むまでこの入口を繰り返し呼ぶ。
/// </para>
/// <para>
/// **無効な間、入口は 404 を返す。** 管理画面から再起動なしで有効にするため、経路は常に登録する
/// （SAML と同じ）。
/// </para>
/// <para>
/// **失敗の理由は決まった印だけを返す。** 詳しい理由はサーバのログと操作の記録に残す。
/// </para>
/// </remarks>
public static class AdminPleasanterSsoEndpoints
{
    /// <summary>確認の入口に掛けるレート制限の名前。</summary>
    /// <remarks>
    /// **ログインの試行の枠（5 分に 10 回）とは分ける。** 画面は Pleasanter でのログインを
    /// 待つ間この入口を繰り返し呼ぶため、同じ枠だと数十秒で使い切り、合言葉のログインまで止まる。
    /// ここで総当たりできるものは無い（資格情報は Pleasanter の cookie そのもの）が、
    /// Pleasanter へ問い合わせを増幅させないために枠は掛ける。
    /// </remarks>
    public const string CheckRateLimitPolicy = "admin-pleasanter-sso";

    /// <summary><see cref="CheckRateLimitPolicy"/> の 5 分あたりの上限。**画面の問い合わせ間隔（2 秒）に余裕を持たせる。**</summary>
    public const int CheckRequestsPer5Minutes = 300;

    public static IEndpointRouteBuilder MapAdminPleasanterSsoEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var group = builder.MapGroup("/api/admin/pleasanter-sso")
            .WithTags("管理 API");
        AdminAuthSchemes.AddNoStore(group);

        // **入れた・断られたを記録に残す**（Issue #19）。
        // 「まだ Pleasanter にログインしていない」だけの問い合わせは残さない（AuditNotes.Skip）
        group.AddEndpointFilter<AuditLogFilter>();

        // **本文は JSON で受ける（中身は空でよい）。** 他所のサイトの form からは
        // application/json を送れないので、別サイトから勝手にログインさせられない
        group.MapPost("/check", async (
            PleasanterSsoCheckRequest request,
            HttpContext context,
            IAdminUserStore store,
            IPleasanterSsoOptionsProvider optionsProvider,
            IPleasanterSessionVerifier verifier,
            PleasanterSsoAuthenticator authenticator,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            if (!options.Enabled)
            {
                AuditNotes.Skip(context);
                return Results.NotFound();
            }

            // **最初の管理者を Pleasanter から作らせない。** 誰でも全権を取れてしまう
            // （SAML の釦を最初の管理者を作る画面に出さないのと同じ理由）
            if (await store.IsEmptyAsync(cancellationToken).ConfigureAwait(false))
            {
                AuditNotes.Add(context, "result", "setup-required");
                return Results.Conflict(new { code = "setup-required" });
            }

            var verification = await verifier
                .VerifyAsync(context.Request.Headers.Cookie, options, cancellationToken)
                .ConfigureAwait(false);

            switch (verification.Status)
            {
                case PleasanterSessionStatus.Unauthenticated:
                    // **まだログインしていないだけ。** 画面は待つ間これを繰り返すので記録しない
                    AuditNotes.Skip(context);
                    return Results.Ok(new { status = "unauthenticated" });

                case PleasanterSessionStatus.NotAllowed:
                    AuditNotes.Add(context, "result", "not-allowed");
                    AuditNotes.Add(context, "reason", verification.Reason);
                    return Results.Json(new { code = "not-allowed" }, statusCode: StatusCodes.Status403Forbidden);

                case PleasanterSessionStatus.UpstreamError:
                    AuditNotes.Add(context, "result", "upstream-error");
                    AuditNotes.Add(context, "reason", verification.Reason);
                    return Results.Json(
                        new { code = "upstream-error" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var identity = verification.Identity!;

            // **誰が来たかを記録に残す**
            AuditNotes.Add(context, "loginId", identity.LoginId);
            AuditNotes.Add(context, "pleasanterTenantId", identity.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AuditNotes.Add(context, "pleasanterUserId", identity.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var result = await authenticator.SignInAsync(identity.LoginId, options, cancellationToken)
                .ConfigureAwait(false);

            if (result.Registered)
            {
                AuditNotes.Add(context, "pleasanterSsoRegistered", "true");
            }

            var claims = PleasanterSsoClaims.Create(identity, timeProvider.GetUtcNow());

            switch (result.Outcome)
            {
                case PleasanterSsoSignInOutcome.SignedIn:
                    AuditNotes.Add(context, "result", "signed-in");
                    await AdminAuthEndpoints.SignInSessionAsync(context, result.User!, extraClaims: claims)
                        .ConfigureAwait(false);
                    return Results.Ok(new { status = "signedIn", next = "done" });

                case PleasanterSsoSignInOutcome.NeedsSecondFactor:
                    // **本アプリで 2 要素を登録済みなら通す**（SAML と同じ）
                    AuditNotes.Add(context, "result", "second-factor");
                    await AdminAuthEndpoints.SignInPendingAsync(context, result.User!, secret: null, claims)
                        .ConfigureAwait(false);
                    return Results.Ok(new { status = "signedIn", next = "totp" });

                case PleasanterSsoSignInOutcome.NeedsTotpEnrollment:
                    AuditNotes.Add(context, "result", "enroll");
                    await AdminAuthEndpoints.SignInPendingAsync(context, result.User!, secret: null, claims)
                        .ConfigureAwait(false);
                    return Results.Ok(new { status = "signedIn", next = "enroll" });

                case PleasanterSsoSignInOutcome.NotAllowed:
                    AuditNotes.Add(context, "result", "not-allowed");
                    AuditNotes.Add(context, "reason", "membership-not-configured");
                    return Results.Json(new { code = "not-allowed" }, statusCode: StatusCodes.Status403Forbidden);

                case PleasanterSsoSignInOutcome.Disabled:
                    AuditNotes.Add(context, "result", "disabled");
                    return Results.Json(new { code = "disabled" }, statusCode: StatusCodes.Status403Forbidden);

                default:
                    AuditNotes.Add(context, "result", "unknown-user");
                    return Results.Json(new { code = "unknown-user" }, statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequireRateLimiting(CheckRateLimitPolicy);

        MapSettings(group);

        return builder;
    }

    /// <summary>特権管理者だけが使える設定の入口。</summary>
    private static void MapSettings(RouteGroupBuilder group)
    {
        var policy = AdminPermissions.PolicyOf(AdminPermissions.PleasanterSsoSettings);

        group.MapGet("/settings", async (
            IPleasanterSsoOptionsProvider provider,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(SettingsBody(snapshot));
        }).RequireAuthorization(policy);

        group.MapPut("/settings", async (
            PleasanterSsoSettingsRequest request,
            HttpContext context,
            IPleasanterSsoOptionsProvider provider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var before = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
                var changedFields = ChangedFields(before, request);
                if (changedFields.Count > 0)
                {
                    // **どの項目を変えたかを残す。** 秘密の値は無いが、SAML と同じく名前だけ残す
                    AuditNotes.Add(context, "changedFields", string.Join(",", changedFields));
                }

                var snapshot = await provider
                    .SaveAsync(request.ToValues(), cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(SettingsBody(snapshot));
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        }).RequireAuthorization(policy);

        // ---- 接続の試験 ------------------------------------------------------
        // **保存前の値で、いまのブラウザの Pleasanter の cookie を使って問い合わせる。**
        // 同じブラウザで Pleasanter にログインしていれば、その人が返る。
        // 有効にする前に「内部 URL・cookie の転送・API 禁止時の API キー」が揃っているかを確かめられる
        group.MapPost("/settings/test", async (
            PleasanterSsoSettingsRequest request,
            HttpContext context,
            IPleasanterSsoOptionsProvider provider,
            IPleasanterSessionVerifier verifier,
            IAdminUserStore store,
            CancellationToken cancellationToken) =>
        {
            PleasanterSsoOptions options;
            try
            {
                // **有効かどうかに関わらず試せるようにする。** 有効にする前に確かめたい
                options = (await provider
                        .PreviewAsync(request.ToValues(), forceEnabled: true, cancellationToken)
                        .ConfigureAwait(false))
                    .Options;
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }

            var verification = await verifier
                .VerifyAsync(context.Request.Headers.Cookie, options, cancellationToken)
                .ConfigureAwait(false);
            AuditNotes.Add(context, "result", verification.Status.ToString());

            if (verification.Identity is not { } identity)
            {
                return Results.Ok(new
                {
                    status = verification.Status.ToString(),
                    reason = verification.Reason,
                });
            }

            var matched = await store.FindByLoginIdAsync(identity.LoginId, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                status = verification.Status.ToString(),
                reason = verification.Reason,
                tenantId = identity.TenantId,
                userId = identity.UserId,
                loginId = identity.LoginId,
                name = identity.Name,
                registered = matched is not null,
                registrationAllowed = options.UnknownUser == PleasanterSsoUnknownUserPolicy.Register
                    && options.HasMembershipRestriction,
            });
        }).RequireAuthorization(policy).RequireRateLimiting(CheckRateLimitPolicy);
    }

    private static List<string> ChangedFields(
        PleasanterSsoOptionsSnapshot before,
        PleasanterSsoSettingsRequest request)
    {
        var old = before.Values;
        var next = request.ToValues();
        var changed = new List<string>();

        void Add(string name, string key, string? previous, string? requested)
        {
            if (!before.FixedKeys.Contains(key)
                && !string.Equals(previous?.Trim(), requested?.Trim(), StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        Add("allowedDeptIds", PleasanterSsoOptions.AllowedDeptIdsKey, old.AllowedDeptIds, next.AllowedDeptIds);
        Add("allowedGroupIds", PleasanterSsoOptions.AllowedGroupIdsKey, old.AllowedGroupIds, next.AllowedGroupIds);
        Add("enabled", PleasanterSsoOptions.EnabledKey, old.Enabled, next.Enabled);
        Add("internalBaseUrl", PleasanterSsoOptions.InternalBaseUrlKey, old.InternalBaseUrl, next.InternalBaseUrl);
        Add("loginUrl", PleasanterSsoOptions.LoginUrlKey, old.LoginUrl, next.LoginUrl);
        Add("logoutUrl", PleasanterSsoOptions.LogoutUrlKey, old.LogoutUrl, next.LogoutUrl);
        Add("cookieNames", PleasanterSsoOptions.CookieNamesKey, old.CookieNames, next.CookieNames);
        Add("unknownUser", PleasanterSsoOptions.UnknownUserKey, old.UnknownUser, next.UnknownUser);
        Add("registerRole", PleasanterSsoOptions.RegisterRoleKey, old.RegisterRole, next.RegisterRole);
        Add(
            "revalidateMinutes",
            PleasanterSsoOptions.RevalidateMinutesKey,
            old.RevalidateMinutes,
            next.RevalidateMinutes);
        Add("timeoutSeconds", PleasanterSsoOptions.TimeoutSecondsKey, old.TimeoutSeconds, next.TimeoutSeconds);
        Add("buttonLabel", PleasanterSsoOptions.ButtonLabelKey, old.ButtonLabel, next.ButtonLabel);

        return changed;
    }

    private static object SettingsBody(PleasanterSsoOptionsSnapshot snapshot)
    {
        bool Fixed(string key) => snapshot.FixedKeys.Contains(key);
        var values = snapshot.Values;

        return new
        {
            enabled = string.Equals(values.Enabled?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            internalBaseUrl = values.InternalBaseUrl,
            loginUrl = values.LoginUrl,
            logoutUrl = values.LogoutUrl,
            cookieNames = values.CookieNames,
            allowedDeptIds = values.AllowedDeptIds,
            allowedGroupIds = values.AllowedGroupIds,
            unknownUser = values.UnknownUser,
            registerRole = values.RegisterRole,
            revalidateMinutes = values.RevalidateMinutes,
            timeoutSeconds = values.TimeoutSeconds,
            buttonLabel = values.ButtonLabel,
            fixedFields = new
            {
                enabled = Fixed(PleasanterSsoOptions.EnabledKey),
                internalBaseUrl = Fixed(PleasanterSsoOptions.InternalBaseUrlKey),
                loginUrl = Fixed(PleasanterSsoOptions.LoginUrlKey),
                logoutUrl = Fixed(PleasanterSsoOptions.LogoutUrlKey),
                allowedDeptIds = Fixed(PleasanterSsoOptions.AllowedDeptIdsKey),
                allowedGroupIds = Fixed(PleasanterSsoOptions.AllowedGroupIdsKey),
                cookieNames = Fixed(PleasanterSsoOptions.CookieNamesKey),
                unknownUser = Fixed(PleasanterSsoOptions.UnknownUserKey),
                registerRole = Fixed(PleasanterSsoOptions.RegisterRoleKey),
                revalidateMinutes = Fixed(PleasanterSsoOptions.RevalidateMinutesKey),
                timeoutSeconds = Fixed(PleasanterSsoOptions.TimeoutSecondsKey),
                buttonLabel = Fixed(PleasanterSsoOptions.ButtonLabelKey),
            },
        };
    }

    /// <summary>確認の入口の本文。**中身は使わない**（JSON で送らせるためだけにある）。</summary>
    public sealed record PleasanterSsoCheckRequest;

    /// <summary>画面から来る設定。</summary>
    public sealed record PleasanterSsoSettingsRequest(
        bool Enabled,
        string? InternalBaseUrl,
        string? LoginUrl,
        string? LogoutUrl,
        string? CookieNames,
        string? UnknownUser,
        string? RegisterRole,
        string? RevalidateMinutes,
        string? TimeoutSeconds,
        string? ButtonLabel,
        string? AllowedDeptIds = null,
        string? AllowedGroupIds = null)
    {
        public PleasanterSsoSettingValues ToValues() => new()
        {
            Enabled = Enabled ? "true" : "false",
            InternalBaseUrl = InternalBaseUrl,
            LoginUrl = LoginUrl,
            LogoutUrl = LogoutUrl,
            CookieNames = CookieNames,
            AllowedDeptIds = AllowedDeptIds,
            AllowedGroupIds = AllowedGroupIds,
            UnknownUser = UnknownUser,
            RegisterRole = RegisterRole,
            RevalidateMinutes = RevalidateMinutes,
            TimeoutSeconds = TimeoutSeconds,
            ButtonLabel = ButtonLabel,
        };
    }
}
