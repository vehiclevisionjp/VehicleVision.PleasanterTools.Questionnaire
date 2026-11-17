using System.Security.Claims;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>管理 API の個人トークン（API キー）を発行・一覧・失効する入口（Issue #1025。親 #1012）。</summary>
/// <remarks>
/// <para>
/// <b>ブラウザのセッション（<see cref="AdminAuthSchemes.Session"/>）だけで開く。</b>トークンでトークンは作れない・失効できない
/// （#1026 の Bearer 認証が入っても、この群へは許可を宣言しないこと）。CSRF の対象のまま。
/// </para>
/// <para>
/// <b>平文のトークンを返すのは、発行の応答の 1 回だけ。</b>一覧・失効・監査・ログには値を出さない。
/// 監査へ預けるのは名前・接頭辞・スコープ・期限・許可リストだけ（<see cref="AuditNotes"/>）。
/// 応答は <see cref="AdminAuthSchemes.AddNoStore"/> で、途中の経路に残させない。
/// </para>
/// </remarks>
public static class AdminApiTokenEndpoints
{
    /// <summary>トークンの識別子の経路。監査の対象の見分け（<c>AuditLogFilter</c>）と同じ名前にする。</summary>
    public const string TokenIdRouteKey = "adminApiTokenId";

    public static IEndpointRouteBuilder MapAdminApiTokenEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin")
            .WithTags("管理 API");
        AdminAuthSchemes.AddNoStore(group);

        // **発行・失効を残す**（Issue #19）。読み取りは残さない。値は残らない（本文に触らない）
        group.AddEndpointFilter<AuditLogFilter>();

        MapSelf(group);
        MapOthers(group);

        return builder;
    }

    // ---- 自分のトークン（役割を問わない。発行できる役割かどうかはサービスが見る） ------
    private static void MapSelf(RouteGroupBuilder parent)
    {
        var me = parent.MapGroup("/me/api-tokens").RequireAuthorization(AdminAuthSchemes.SessionPolicy);

        // ---- 一覧 + 画面が出す情報 ----------------------------------------------
        // **機能が無効でも使える。** 漏れたトークンを止められなくならないように
        me.MapGet("", async (
            ClaimsPrincipal principal,
            AdminApiTokenService service,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var role = principal.FindFirstValue(ClaimTypes.Role);
            var list = await service.ListAsync(ActorId(principal), cancellationToken).ConfigureAwait(false);
            var viaSso = IsSsoSession(principal);

            return Results.Ok(new
            {
                enabled = service.Enabled,
                // **発行できる役割か**と、**付けられるスコープ**（持ち主の役割 ∩ 許可リスト）。画面が出し分ける
                canIssue = service.Enabled && AdminApiTokenScopes.CanIssue(role),
                issuableScopes = AdminApiTokenScopes.IssuableFor(role),
                defaultExpiresInDays = service.Options.DefaultDays,
                maxExpiresInDays = service.Options.MaxDays,
                maxIpAllowListEntries = AdminApiTokenOptions.MaxIpAllowListEntries,
                // 本人確認の手段。**SSO の管理者はパスワードを持たないので、直前の再ログイン**
                reauthentication = viaSso ? "relogin" : "password",
                reloginMinutes = (int)service.Options.ReloginWindow.TotalMinutes,
                tokens = list.Select(token => View(token, time.GetUtcNow().UtcDateTime)).ToList(),
            });
        });

        // ---- 発行 ----------------------------------------------------------------
        me.MapPost("", async (
            AdminApiTokenIssueBody body,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminApiTokenService service,
            IAdminSessionStore sessions,
            CancellationToken cancellationToken) =>
        {
            var language = RequestLanguage.Of(context);
            var ownerId = ActorId(principal);

            // **本人確認の手段。** SSO で入った管理者はパスワードを持たない（JIT で作る）ので、直前の再ログインを求める。
            // それ以外はパスワードを確かめ直す
            AdminApiTokenReauthentication reauthentication;
            if (IsSsoSession(principal))
            {
                var createdAt = await SessionCreatedAtAsync(principal, ownerId, sessions, cancellationToken)
                    .ConfigureAwait(false);
                reauthentication = new AdminApiTokenReauthentication.Relogin(createdAt ?? DateTimeOffset.MinValue);
            }
            else
            {
                reauthentication = new AdminApiTokenReauthentication.Password(body.Password);
            }

            var (outcome, issued) = await service
                .IssueAsync(
                    ownerId,
                    new AdminApiTokenIssueRequest(body.Name, body.Scopes, body.ExpiresInDays, body.IpAllowList),
                    reauthentication,
                    cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not AdminApiTokenIssueOutcome.Issued || issued is null)
            {
                return Failure(outcome, service.Options, language);
            }

            // ⚠️ **監査へ預けるのは識別と条件だけ。トークンの値は預けない。**
            var record = issued.Record;
            AuditNotes.SetTarget(context, "AdminApiToken", record.TokenId.ToString());
            AuditNotes.Add(context, "name", record.Name);
            AuditNotes.Add(context, "prefix", record.Prefix);
            AuditNotes.Add(context, "scopes", string.Join(' ', record.Scopes));
            AuditNotes.Add(context, "expiresAt", record.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            AuditNotes.Add(context, "ipAllowList", string.Join(',', record.IpAllowList));

            return Results.Ok(new
            {
                // **返せるのはこの時だけ。** 保存しているのはハッシュのみ。再表示はできない
                token = issued.Token,
                apiToken = View(record, record.CreatedAt),
            });
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- 失効（本人） --------------------------------------------------------
        me.MapPost("/{" + TokenIdRouteKey + ":guid}/revoke", async (
            Guid adminApiTokenId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminApiTokenService service,
            CancellationToken cancellationToken) =>
        {
            var actorId = ActorId(principal);
            var revoked = await service
                .RevokeAsync(adminApiTokenId, actorId, actorId, AdminApiTokenRevokeReasons.Owner, cancellationToken)
                .ConfigureAwait(false);
            return RevokedResult(revoked, context);
        });
    }

    // ---- 他人のトークン（ユーザ管理。発行はできない） --------------------------------
    private static void MapOthers(RouteGroupBuilder parent)
    {
        var users = parent.MapGroup("/users/{adminUserId:guid}/api-tokens")
            .RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.UsersRead));

        users.MapGet("", async (
            Guid adminUserId,
            AdminApiTokenService service,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var list = await service.ListAsync(adminUserId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                tokens = list.Select(token => View(token, time.GetUtcNow().UtcDateTime)).ToList(),
            });
        });

        // **漏えい時の止め方。** 他人のトークンを失効できるのは、管理者を触れる人（users.write）
        users.MapPost("/{" + TokenIdRouteKey + ":guid}/revoke", async (
            Guid adminUserId,
            Guid adminApiTokenId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminApiTokenService service,
            CancellationToken cancellationToken) =>
        {
            var revoked = await service
                .RevokeAsync(
                    adminApiTokenId,
                    adminUserId,
                    ActorId(principal),
                    adminUserId == ActorId(principal)
                        ? AdminApiTokenRevokeReasons.Owner
                        : AdminApiTokenRevokeReasons.Administrator,
                    cancellationToken)
                .ConfigureAwait(false);
            return RevokedResult(revoked, context);
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.UsersWrite));

        users.MapPost("/revoke-all", async (
            Guid adminUserId,
            HttpContext context,
            ClaimsPrincipal principal,
            AdminApiTokenService service,
            CancellationToken cancellationToken) =>
        {
            var revoked = await service
                .RevokeAllAsync(
                    adminUserId,
                    ActorId(principal),
                    adminUserId == ActorId(principal)
                        ? AdminApiTokenRevokeReasons.Owner
                        : AdminApiTokenRevokeReasons.Administrator,
                    cancellationToken)
                .ConfigureAwait(false);
            NoteRevoked(context, revoked);
            return Results.Ok(new { revoked = revoked.Count });
        }).RequireAuthorization(AdminPermissions.PolicyOf(AdminPermissions.UsersWrite));
    }

    /// <summary>
    /// パスワードの変更・2 要素の解除に合わせて、持ち主の API キーを失効させ、監査に残す。
    /// <b>確認ダイアログの「この人の API キーも失効させる」（初期値オン）</b>に対応する。
    /// </summary>
    /// <param name="revoke">
    /// 画面のチェック。<b>指定が無ければ失効させる</b>（古い呼び出し元・直接の API 利用でも、安全な側に倒す）。
    /// </param>
    /// <returns>失効させた本数。</returns>
    internal static async Task<int> RevokeOnCredentialChangeAsync(
        HttpContext context,
        AdminApiTokenService service,
        Guid ownerId,
        Guid revokedBy,
        bool? revoke,
        string reason,
        CancellationToken cancellationToken)
    {
        if (revoke is false)
        {
            return 0;
        }

        var revoked = await service.RevokeAllAsync(ownerId, revokedBy, reason, cancellationToken).ConfigureAwait(false);
        NoteRevoked(context, revoked);
        return revoked.Count;
    }

    /// <summary>失効させたトークンの接頭辞と本数を、監査へ預ける。<b>値は預けない。</b></summary>
    private static void NoteRevoked(HttpContext context, IReadOnlyList<AdminApiTokenRecord> revoked)
    {
        if (revoked.Count == 0)
        {
            return;
        }

        AuditNotes.Add(context, "apiTokensRevoked", revoked.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AuditNotes.Add(context, "apiTokenPrefixes", string.Join(',', revoked.Select(token => token.Prefix)));
    }

    private static IResult RevokedResult(AdminApiTokenRecord? revoked, HttpContext context)
    {
        if (revoked is null)
        {
            // **持ち主の組で引く。** 他人の識別子・無い識別子・すでに失効しているものは、同じ「見つからない」
            return Results.NotFound(new
            {
                message = ServerMessages.Get(ServerMessageKeys.ApiTokenNotFound, RequestLanguage.Of(context)),
            });
        }

        AuditNotes.Add(context, "prefix", revoked.Prefix);
        AuditNotes.Add(context, "name", revoked.Name);
        return Results.Ok(new { revoked = true });
    }

    private static IResult Failure(AdminApiTokenIssueOutcome outcome, AdminApiTokenOptions options, string language)
    {
        string Message(string key, params object[] arguments) => ServerMessages.Get(key, language, arguments);

        return outcome switch
        {
            AdminApiTokenIssueOutcome.FeatureDisabled => Results.Json(
                new { message = Message(ServerMessageKeys.ApiTokenFeatureDisabled) },
                statusCode: StatusCodes.Status403Forbidden),

            AdminApiTokenIssueOutcome.RoleNotAllowed => Results.Json(
                new { message = Message(ServerMessageKeys.ApiTokenRoleNotAllowed) },
                statusCode: StatusCodes.Status403Forbidden),

            AdminApiTokenIssueOutcome.OwnerUnavailable => Results.NotFound(
                new { message = Message(ServerMessageKeys.AdminUserNotFound) }),

            AdminApiTokenIssueOutcome.NameInvalid => Results.BadRequest(
                new { message = Message(ServerMessageKeys.ApiTokenNameInvalid) }),

            AdminApiTokenIssueOutcome.ScopesInvalid => Results.BadRequest(
                new { message = Message(ServerMessageKeys.ApiTokenScopesInvalid) }),

            AdminApiTokenIssueOutcome.ScopeExceedsRole => Results.Json(
                new { message = Message(ServerMessageKeys.ApiTokenScopeExceedsRole) },
                statusCode: StatusCodes.Status403Forbidden),

            AdminApiTokenIssueOutcome.ExpiryInvalid => Results.BadRequest(
                new { message = Message(ServerMessageKeys.ApiTokenExpiryInvalid, options.MaxDays) }),

            AdminApiTokenIssueOutcome.IpAllowListInvalid => Results.BadRequest(
                new
                {
                    message = Message(
                        ServerMessageKeys.ApiTokenIpAllowListInvalid, AdminApiTokenOptions.MaxIpAllowListEntries),
                }),

            AdminApiTokenIssueOutcome.LimitReached => Results.Conflict(
                new
                {
                    message = Message(
                        ServerMessageKeys.ApiTokenLimitReached, AdminApiTokenOptions.MaxActivePerOwner),
                }),

            AdminApiTokenIssueOutcome.LockedOut => Results.Json(
                new { message = Message(ServerMessageKeys.OperationTemporarilyLocked) },
                statusCode: StatusCodes.Status423Locked),

            AdminApiTokenIssueOutcome.ReloginRequired => Results.Json(
                new
                {
                    message = Message(
                        ServerMessageKeys.ApiTokenReloginRequired, (int)options.ReloginWindow.TotalMinutes),
                },
                statusCode: StatusCodes.Status403Forbidden),

            _ => Results.Json(
                new { message = Message(ServerMessageKeys.CurrentPasswordRejected) },
                statusCode: StatusCodes.Status401Unauthorized),
        };
    }

    /// <summary>1 本分の表示。<b>ハッシュも平文も含めない。</b></summary>
    private static object View(AdminApiTokenRecord token, DateTime nowUtc) => new
    {
        adminApiTokenId = token.TokenId,
        name = token.Name,
        prefix = token.Prefix,
        scopes = token.Scopes,
        ipAllowList = token.IpAllowList,
        createdAt = token.CreatedAt,
        expiresAt = token.ExpiresAt,
        revokedAt = token.RevokedAt,
        revokeReason = token.RevokeReason,
        lastUsedAt = token.LastUsedAt,
        lastUsedIp = token.LastUsedIp,
        // active / revoked / expired。**失効を優先**する（失効してから期限が来ても「失効」）
        status = token.RevokedAt is not null ? "revoked" : token.ExpiresAt <= nowUtc ? "expired" : "active",
    };

    /// <summary>SSO（SAML・Pleasanter のログイン）で入ったセッションか。</summary>
    private static bool IsSsoSession(ClaimsPrincipal principal) =>
        !string.IsNullOrEmpty(principal.FindFirstValue(AdminAuthSchemes.SamlNameIdClaim))
        || principal.FindFirst(PleasanterSsoClaims.UserId) is not null;

    /// <summary>このセッションを作った時刻（IdP を通って入り直した時刻）。</summary>
    private static async Task<DateTimeOffset?> SessionCreatedAtAsync(
        ClaimsPrincipal principal,
        Guid ownerId,
        IAdminSessionStore sessions,
        CancellationToken cancellationToken)
    {
        if (principal.FindFirstValue(AdminSessionManager.SessionIdClaim) is not { Length: > 0 } sessionId)
        {
            return null;
        }

        var handle = sessions.HandleOf(sessionId);
        var list = await sessions.ListForUserAsync(ownerId, cancellationToken).ConfigureAwait(false);
        return list.FirstOrDefault(session => string.Equals(session.Handle, handle, StringComparison.Ordinal))
            ?.CreatedAt;
    }

    private static Guid ActorId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>発行の依頼。<b>パスワードは本文で受け取る</b>（URL に載せない）。</summary>
    public sealed record AdminApiTokenIssueBody(
        string? Name,
        IReadOnlyList<string>? Scopes,
        int? ExpiresInDays,
        IReadOnlyList<string>? IpAllowList,
        string? Password);
}
