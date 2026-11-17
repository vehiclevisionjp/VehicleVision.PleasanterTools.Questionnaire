using System.Security.Claims;
using System.Xml;
using Microsoft.IdentityModel.Tokens.Saml2;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.Cryptography;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>SAML 2.0 で管理画面へ入る入口（Issue #166）。</summary>
/// <remarks>
/// <para>
/// **SP 起動だけを受ける。** ブラウザは
/// <c>GET /api/admin/saml/login</c> → IdP → <c>POST /api/admin/saml/acs</c> と回る。
/// **IdP 起動（こちらが出していない応答）は受け取らない。**
/// 送り付けられた応答で入られると、誰がいつ入ったのかを追えなくなる。
/// </para>
/// <para>
/// ⚠️ **署名の検証は <see cref="Saml2Binding.Unbind"/> が行う。**
/// それより前に読めた値（<c>Status</c> など）は、まだ**誰が書いたか分からない値**。
/// 通す・通さないの判断は Unbind の後の値だけで行う。
/// </para>
/// <para>
/// **失敗の理由は URL の印だけで返す。** 画面には決まった文言を出し、
/// 詳しい理由はサーバのログと操作の記録に残す。
/// </para>
/// <para>
/// **SAML が無効な間、認証の入口は 404 を返す。**
/// 管理画面から再起動なしで有効化するため、経路自体は常に登録する。
/// </para>
/// </remarks>
public static class AdminSamlEndpoints
{
    /// <summary>やり取りの途中を預ける cookie。</summary>
    /// <remarks>
    /// **IdP からの他サイト POST で戻ってくる必要があるので <c>SameSite=None</c>。**
    /// そのため <c>Secure</c> が要る（＝ HTTPS でしか動かない）。
    /// SAML を使う構成は HTTPS 前提なので、これで困らない。
    /// </remarks>
    private const string RelayCookieName = "q.admin.saml";

    /// <summary>途中を預けておける長さ。**IdP でのログインに掛かる分だけ。**</summary>
    private static readonly TimeSpan RelayLifetime = TimeSpan.FromMinutes(15);

    /// <summary>データの保護に使う用途の名前。</summary>
    private const string ProtectorPurpose = "VehicleVision.Questionnaire.Saml.RelayState";

    /// <summary>
    /// 強制再認証（Issue #1034）の途中を預ける cookie。<b>ログインの cookie（<see cref="RelayCookieName"/>）とは別</b>にする。
    /// ログイン中の別タブのやり取りと、お互いを上書きしない。
    /// </summary>
    private const string ReauthCookieName = "q.admin.saml.reauth";

    private const string ReauthProtectorPurpose = "VehicleVision.Questionnaire.Saml.Reauth";

    public static IEndpointRouteBuilder MapAdminSamlEndpoints(
        this IEndpointRouteBuilder builder,
        AdminPathOptions adminPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(adminPath);

        var group = builder.MapGroup("/api/admin/saml")
            .WithTags("管理 API");
        AdminAuthSchemes.AddNoStore(group);

        // **入れた・断られたを記録に残す**（Issue #19）。GET は残らない
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- SP のメタデータ -------------------------------------------------
        // **IdP へ登録する値を、実際に動いている設定から出す。**
        // 手で書き写すと、EntityID や受け口の URL の食い違いに気付けない
        group.MapGet("/metadata", async (
            HttpContext context,
            ISamlOptionsProvider optionsProvider,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var descriptor = new EntityDescriptor(options.ToSaml2Configuration())
            {
                ValidUntil = 365,
                SPSsoDescriptor = new SPSsoDescriptor
                {
                    // **こちらは AuthnRequest に署名しない**（IdP 側の設定と合わせる）
                    AuthnRequestsSigned = false,

                    // **署名の無いアサーションは受け取らない**
                    WantAssertionsSigned = true,
                    NameIDFormats = [NameIdentifierFormats.Email],
                    AssertionConsumerServices =
                    [
                        new AssertionConsumerService
                        {
                            Binding = ProtocolBindings.HttpPost,
                            Location = AssertionConsumerServiceUrl(context),
                        },
                    ],
                },
            };

            var xml = new Saml2Metadata(descriptor).CreateMetadata().ToXml();
            return Results.Content(xml, "application/samlmetadata+xml");
        });

        // ---- IdP へ送り出す --------------------------------------------------
        group.MapGet("/login", async (
            HttpContext context,
            ISamlOptionsProvider optionsProvider,
            IDataProtectionProvider protectionProvider,
            string? returnUrl,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            var configuration = options.ToSaml2Configuration();
            var request = new Saml2AuthnRequest(configuration)
            {
                Destination = options.SingleSignOnUrl,
                AssertionConsumerServiceUrl = AssertionConsumerServiceUrl(context),
                ProtocolBinding = ProtocolBindings.HttpPost,
            };

            var binding = new Saml2RedirectBinding();
            binding.Bind(request);

            // **出した要求の id を預けておく。** 戻ってきた応答の InResponseTo と
            // 突き合わせて、**こちらが出していない応答を受け取らない**
            SaveRelay(
                context,
                protectionProvider,
                request.IdAsString,
                LocalReturnUrl(returnUrl, AdminEntry(context, adminPath.Path)));

            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- IdP から受け取る ------------------------------------------------
        group.MapPost("/acs", async (
            HttpContext context,
            ISamlOptionsProvider optionsProvider,
            SamlAuthenticator authenticator,
            SamlReauthService reauth,
            IAdminUserStore store,
            IDataProtectionProvider protectionProvider,
            ILogger<SamlAuthenticator> logger,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            if (!options.Enabled)
            {
                return Results.NotFound();
            }

            // **API キーの発行の強制再認証（Issue #1034）の戻りは、先にここで受ける。**
            // 出した要求（cookie が指す id）への応答でなければ、通常のログインとして続ける
            var reauthRequestId = ReadReauthRequestId(context, protectionProvider);
            if (reauthRequestId is not null)
            {
                var reauthResult = await TryCompleteReauthAsync(
                    context, options, reauthRequestId, reauth, adminPath.Path, logger, cancellationToken)
                    .ConfigureAwait(false);
                if (reauthResult is not null)
                {
                    return reauthResult;
                }
            }

            var relay = ReadRelay(context, protectionProvider, AdminEntry(context, adminPath.Path));
            ClearRelay(context);

            if (await store.CountAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return FailedAudited(context, AdminEntry(context, adminPath.Path),
                    SamlFailure.Invalid, "setup-required", StatusCodes.Status409Conflict);
            }

            if (relay is null)
            {
                // **こちらが出した要求と結び付かない応答は受け取らない**
                logger.LogWarning("SAML の応答に、対応する要求が見つかりませんでした。");
                return FailedAudited(
                    context, AdminEntry(context, adminPath.Path), SamlFailure.Invalid, "relay-missing");
            }

            var configuration = options.ToSaml2Configuration();
            var response = new Saml2AuthnResponse(configuration);

            try
            {
                var httpRequest = context.Request.ToGenericHttpRequest(validate: true);
                httpRequest.Binding.ReadSamlResponse(httpRequest, response);

                // ⚠️ **ここでの Status は、まだ署名を確かめていない値。**
                // 先に見るのは、失敗の応答にはアサーションが無く Unbind が通らないため
                if (response.Status != Saml2StatusCodes.Success)
                {
                    logger.LogWarning(
                        "IdP が認証を断りました: {Status}", response.Status);
                    return FailedAudited(
                    context, AdminEntry(context, adminPath.Path), SamlFailure.Invalid, "idp-refused");
                }

                // **署名・発行者・宛先・有効期間を確かめるのはここ**
                httpRequest.Binding.Unbind(httpRequest, response);
            }
            catch (Exception exception) when (exception is Saml2RequestException
                or Saml2BindingException
                or InvalidSignatureException
                or InvalidOperationException
                or ArgumentException
                or FormatException)
            {
                // **理由は外へ返さない。** 何が通らなかったかは手掛かりになる
                logger.LogWarning(exception, "SAML の応答を受け取れませんでした。");
                return FailedAudited(
                    context, AdminEntry(context, adminPath.Path), SamlFailure.Invalid, "response-invalid");
            }

            // **出した要求への応答であることを確かめる**
            if (!string.Equals(response.InResponseToAsString, relay.RequestId, StringComparison.Ordinal))
            {
                logger.LogWarning("SAML の応答が、こちらの出した要求のものではありませんでした。");
                return FailedAudited(
                    context, AdminEntry(context, adminPath.Path), SamlFailure.Invalid, "in-response-to-mismatch");
            }

            var loginId = ReadLoginId(response.ClaimsIdentity, options);
            if (string.IsNullOrWhiteSpace(loginId))
            {
                logger.LogWarning("SAML の応答にログイン ID として使える値がありませんでした。");
                return FailedAudited(
                    context, AdminEntry(context, adminPath.Path), SamlFailure.Invalid, "login-id-missing");
            }

            // **誰が来たかを記録に残す**（AuditNotes は本文へ触らない）
            AuditNotes.Add(context, "loginId", loginId);

            // **署名検証と JIT の方針を同じスナップショットで決める。**
            // 途中で設定が更新されても、1 回の応答に新旧の方針を混ぜない。
            var result = await authenticator.SignInAsync(loginId, options, cancellationToken)
                .ConfigureAwait(false);

            if (result.Registered)
            {
                AuditNotes.Add(context, "samlRegistered", "true");
            }

            switch (result.Outcome)
            {
                case SamlSignInOutcome.SignedIn:
                    AuditNotes.Add(context, "result", "signed-in");
                    // **単一ログアウトに要る手掛かりだけを預ける**（Issue #191）
                    await AdminAuthEndpoints.SignInSessionAsync(
                        context,
                        result.User!.Id,
                        SsoOf(response.ClaimsIdentity))
                        .ConfigureAwait(false);
                    return Results.Redirect(relay.ReturnUrl);

                case SamlSignInOutcome.NeedsSecondFactor:
                case SamlSignInOutcome.NeedsTotpEnrollment:
                    AuditNotes.Add(context, "result", "second-factor");
                    // **2 要素を登録済みなら通し、必須なら登録させる**（Issue #154）。
                    // どちらも「途中状態」で管理画面へ戻し、画面が続きを出す
                    // **IdP へログアウトを頼むのに要る値は、ログイン後のセッションへ引き継ぐ**（Issue #191）
                    await AdminAuthEndpoints.SignInPendingAsync(
                        context, result.User!.Id, SsoOf(response.ClaimsIdentity))
                        .ConfigureAwait(false);
                    return Results.Redirect(relay.ReturnUrl);

                case SamlSignInOutcome.Disabled:
                    return FailedAudited(context, AdminEntry(context, adminPath.Path),
                        SamlFailure.Invalid, "disabled");

                case SamlSignInOutcome.LockedOut:
                    return FailedAudited(context, AdminEntry(context, adminPath.Path),
                        SamlFailure.Invalid, "locked-out", StatusCodes.Status423Locked);

                case SamlSignInOutcome.NotAllowed:
                    return FailedAudited(context, AdminEntry(context, adminPath.Path),
                        SamlFailure.Invalid, "register-role-not-allowed");

                default:
                    return FailedAudited(context, AdminEntry(context, adminPath.Path),
                        SamlFailure.Invalid, "unknown-user");
            }
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        MapReauthStart(group);
        MapSingleLogout(group, adminPath.Path);
        MapSettings(group);

        return builder;
    }

    /// <summary>
    /// API キーの発行の前の、強制再認証を始める（Issue #1034。親 #1012）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ログイン済みの SAML のセッションだけが呼べる。</b><c>POST</c> で、状態を変える要求として
    /// <see cref="AdminCsrfGuard"/>（別オリジンの拒否）の対象のまま。<b>戻り先は呼び出し側から受け取らない</b>
    /// （受け口 <c>/acs</c> は固定の飛び先へ返す。オープンリダイレクトにならない）。
    /// </para>
    /// <para>
    /// 出す要求は <c>ForceAuthn="true"</c>。<b>要求の id・利用者・セッション・時刻をサーバへ預け</b>
    /// （<see cref="SamlReauthService.BeginAsync"/>）、出したブラウザを cookie で結び付ける。
    /// 応答する URL（飛び先）を返すだけで、<b>秘密は載らない</b>（要求は署名しない設計。URL にあるのは要求の id と時刻だけ）。
    /// </para>
    /// </remarks>
    private static void MapReauthStart(RouteGroupBuilder group)
    {
        group.MapPost("/reauth", async (
            HttpContext context,
            ClaimsPrincipal principal,
            ISamlOptionsProvider optionsProvider,
            SamlReauthService reauth,
            AdminApiTokenOptions tokenOptions,
            IAdminSessionStore sessions,
            IDataProtectionProvider protectionProvider,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            var sessionId = principal.FindFirstValue(AdminSessionManager.SessionIdClaim);
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

            // **SAML で入ったセッションだけ。** それ以外は、再認証の対象ではない
            if (!tokenOptions.Enabled
                || !options.Enabled
                || string.IsNullOrEmpty(sessionId)
                || string.IsNullOrEmpty(principal.FindFirstValue(AdminAuthSchemes.SamlNameIdClaim)))
            {
                return Results.Json(
                    new
                    {
                        message = ServerMessages.Get(
                            ServerMessageKeys.ApiTokenReauthUnavailable, RequestLanguage.Of(context)),
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var request = new Saml2AuthnRequest(options.ToSaml2Configuration())
            {
                Destination = options.SingleSignOnUrl,
                AssertionConsumerServiceUrl = AssertionConsumerServiceUrl(context),
                ProtocolBinding = ProtocolBindings.HttpPost,

                // ⚠️ **IdP のセッションを流用させず、その場で認証し直させる。**
                // 流用されたかどうかは、戻りの AuthnInstant で確かめる（IdP が守らないこともある）
                ForceAuthn = true,
            };

            var binding = new Saml2RedirectBinding();
            binding.Bind(request);

            await reauth.BeginAsync(request.IdAsString, userId, sessions.HandleOf(sessionId), cancellationToken)
                .ConfigureAwait(false);
            SaveReauthCookie(context, protectionProvider, request.IdAsString);

            AuditNotes.Add(context, "purpose", SamlReauthService.Purpose);
            return Results.Ok(new { redirectUrl = binding.RedirectLocation.OriginalString });
        })
            .RequireAuthorization(AdminAuthSchemes.SessionPolicy)
            .RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);
    }

    /// <summary>
    /// 強制再認証の戻りを受ける。<b>この cookie の要求への応答でなければ <c>null</c>（通常のログインとして続ける）。</b>
    /// </summary>
    private static async Task<IResult?> TryCompleteReauthAsync(
        HttpContext context,
        SamlOptions options,
        string cookieRequestId,
        SamlReauthService reauth,
        string adminPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var configuration = options.ToSaml2Configuration();
        var response = new Saml2AuthnResponse(configuration);
        ITfoxtec.Identity.Saml2.Http.HttpRequest httpRequest;

        try
        {
            httpRequest = context.Request.ToGenericHttpRequest(validate: true);
            httpRequest.Binding.ReadSamlResponse(httpRequest, response);
        }
        catch (Exception exception) when (IsSamlFailure(exception))
        {
            // 読めない応答は、再認証の戻りではない。通常のログインの扱い（失敗）に任せる
            return null;
        }

        // ⚠️ **ここでの InResponseTo は署名を確かめる前の値。** 経路の選び分けにだけ使い、信用はしない
        if (!string.Equals(response.InResponseToAsString, cookieRequestId, StringComparison.Ordinal))
        {
            return null;
        }

        // **この cookie は 1 回で使い切る**（成功・失敗を問わない）
        ClearReauthCookie(context);

        var entry = AdminEntry(context, adminPath);
        if (response.Status != Saml2StatusCodes.Success)
        {
            logger.LogWarning("IdP が再認証を断りました: {Status}", response.Status);
            return ReauthFailedAudited(context, entry, "idp-refused");
        }

        try
        {
            // **署名・発行者・宛先・有効期間を確かめるのはここ**
            httpRequest.Binding.Unbind(httpRequest, response);
        }
        catch (Exception exception) when (IsSamlFailure(exception))
        {
            logger.LogWarning(exception, "再認証の SAML 応答を受け取れませんでした。");
            return ReauthFailedAudited(context, entry, "response-invalid");
        }

        var facts = new SamlAssertionFacts(
            response.InResponseToAsString,
            ReadLoginId(response.ClaimsIdentity, options),
            AuthnInstantOf(response),
            AuthnContextClassRefOf(response));

        SamlReauthOutcome outcome;
        try
        {
            outcome = await reauth.CompleteAsync(cookieRequestId, facts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **置き場に届かないときは、許可を作らない**（通す方向に倒さない）
            logger.LogError(exception, "再認証の状態の置き場を使えませんでした。");
            return ReauthFailedAudited(context, entry, "state-store-unavailable");
        }

        if (outcome is not SamlReauthOutcome.Granted)
        {
            logger.LogWarning("SAML の再認証を許可しませんでした: {Outcome}", outcome);
            return ReauthFailedAudited(context, entry, ReauthReason(outcome));
        }

        AuditNotes.Add(context, "result", "reauth-granted");
        AuditNotes.Add(context, "purpose", SamlReauthService.Purpose);
        return ReauthReturn(entry, "done");
    }

    private static string ReauthReason(SamlReauthOutcome outcome) => outcome switch
    {
        SamlReauthOutcome.RequestUnknown => "reauth-request-unknown",
        SamlReauthOutcome.RequestMismatch => "reauth-request-mismatch",
        SamlReauthOutcome.UserMismatch => "reauth-user-mismatch",
        SamlReauthOutcome.AuthnInstantMissing => "reauth-authn-instant-missing",
        SamlReauthOutcome.AuthnInstantStale => "reauth-authn-instant-stale",
        SamlReauthOutcome.AuthnInstantInFuture => "reauth-authn-instant-future",
        SamlReauthOutcome.PreviousSession => "reauth-previous-session",
        _ => "reauth-failed",
    };

    /// <summary>失敗の理由を監査へ残して、固定の飛び先へ戻す。<b>理由は画面へ返さない。</b></summary>
    private static IResult ReauthFailedAudited(HttpContext context, string entry, string reason)
    {
        AuditNotes.Add(context, "result", "reauth-failed");
        AuditNotes.Add(context, "reason", reason);
        AuditNotes.SetStatus(context, StatusCodes.Status401Unauthorized);
        return ReauthReturn(entry, "failed");
    }

    /// <summary>
    /// 管理画面へ戻す。<b>飛び先は固定</b>（管理画面の入口 + 結果の印だけ。呼び出し側の入力を使わない）。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>302 ではなく、ページの側から遷移させる。</b>管理者のセッションの cookie は <c>SameSite=Strict</c> で、
    /// IdP からの他サイトの POST に続く 302 の先へは、ブラウザが送らないことがある
    /// （画面がログアウトに見える）。このページ自身（同じサイト）からの遷移なら送られる。
    /// </remarks>
    private static IResult ReauthReturn(string entry, string result)
    {
        var target = $"{entry}?apiTokenReauth={result}";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\">"
            + $"<meta http-equiv=\"refresh\" content=\"0;url={target}\">"
            + "<meta name=\"referrer\" content=\"no-referrer\"><title></title></head>"
            + $"<body><a href=\"{target}\">Continue</a></body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>応答のアサーションから、IdP が認証した時刻を取り出す。<b>複数あれば最も古いもの。</b>無ければ <c>null</c>。</summary>
    private static DateTimeOffset? AuthnInstantOf(Saml2AuthnResponse response)
    {
        var instants = (response.Saml2SecurityToken?.Assertion?.Statements ?? [])
            .OfType<Saml2AuthenticationStatement>()
            .Select(statement => statement.AuthenticationInstant)
            .ToList();
        if (instants.Count == 0)
        {
            return null;
        }

        var oldest = instants.Min();
        return new DateTimeOffset(
            oldest.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(oldest, DateTimeKind.Utc) : oldest.ToUniversalTime());
    }

    private static string? AuthnContextClassRefOf(Saml2AuthnResponse response) =>
        (response.Saml2SecurityToken?.Assertion?.Statements ?? [])
            .OfType<Saml2AuthenticationStatement>()
            .Select(statement => statement.AuthenticationContext?.ClassReference?.OriginalString)
            .FirstOrDefault(value => value is not null);

    private static void SaveReauthCookie(HttpContext context, IDataProtectionProvider provider, string requestId) =>
        context.Response.Cookies.Append(
            ReauthCookieName,
            provider.CreateProtector(ReauthProtectorPurpose).Protect(requestId),
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.None,
                Secure = true,
                IsEssential = true,
                Path = SamlCookiePath(context),
                MaxAge = SamlReauthService.PendingLifetime,
            });

    private static string? ReadReauthRequestId(HttpContext context, IDataProtectionProvider provider)
    {
        if (!context.Request.Cookies.TryGetValue(ReauthCookieName, out var payload) || string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            var requestId = provider.CreateProtector(ReauthProtectorPurpose).Unprotect(payload);
            return requestId.Length > 0 ? requestId : null;
        }
        catch (Exception exception) when (exception is FormatException
            or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static void ClearReauthCookie(HttpContext context) =>
        context.Response.Cookies.Delete(ReauthCookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.None,
            Secure = true,
            Path = SamlCookiePath(context),
        });

    /// <summary>単一ログアウト（Issue #191）。</summary>
    /// <remarks>
    /// <para>
    /// **SP 起点と IdP 起点の両方を受ける。** 受け口は 1 つで、
    /// 来たものが要求（<c>SAMLRequest</c>）か応答（<c>SAMLResponse</c>）かで分ける。
    /// </para>
    /// <para>
    /// ⚠️ **本アプリの cookie は必ず先に消す。** IdP との往復が途中で切れても、
    /// **こちら側が入れたままになるのが一番まずい。**
    /// </para>
    /// <para>
    /// **SLO を設定していなければ、頼む相手が居ないだけ。**
    /// 対応していない IdP では、これまでどおり cookie を消して終わる。
    /// </para>
    /// </remarks>
    private static void MapSingleLogout(RouteGroupBuilder group, string adminPath)
    {
        // ---- SP 起点：IdP へログアウトを頼む ---------------------------------
        group.MapGet("/logout", async (
            HttpContext context,
            ISamlOptionsProvider optionsProvider,
            IDataProtectionProvider protectionProvider,
            ILogger<SamlAuthenticator> logger,
            CancellationToken cancellationToken) =>
        {
            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            var keys = ReadSessionKeys(context.User.Identity as ClaimsIdentity);

            // ⚠️ **先に落とす。** IdP へ行けなくても、こちらは確実にログアウトする
            var sessions = context.RequestServices.GetRequiredService<AdminSessionManager>();
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Session).ConfigureAwait(false);
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Pending).ConfigureAwait(false);

            if (!options.SingleLogoutEnabled || keys is null)
            {
                // **SAML で入った人でなければ、頼む相手が居ない**
                return Results.Redirect(AdminEntry(context, adminPath));
            }

            var configuration = options.ToSaml2Configuration();
            var request = new Saml2LogoutRequest(configuration)
            {
                Destination = options.SingleLogoutUrl,
                NameId = new Saml2NameIdentifier(keys.NameId),
                SessionIndex = keys.SessionIndex,
            };

            var binding = new Saml2RedirectBinding();
            binding.Bind(request);

            // **出した要求の id を預ける。** 戻ってきた応答と突き合わせる
            SaveRelay(context, protectionProvider, request.IdAsString, AdminEntry(context, adminPath));

            logger.LogInformation("IdP へ単一ログアウトを頼みました。");

            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- IdP 起点と、頼んだぶんの返事 ------------------------------------
        var slo = async (
            HttpContext context,
            ISamlOptionsProvider optionsProvider,
            IDataProtectionProvider protectionProvider,
            ILogger<SamlAuthenticator> logger,
            CancellationToken cancellationToken) =>
        {
            // ⚠️ **署名を確かめる前に落とす。** 偽の要求で落とされても
            // 「ログアウトさせられる」だけで、入られるより軽い
            var sessions = context.RequestServices.GetRequiredService<AdminSessionManager>();
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Session).ConfigureAwait(false);
            await sessions.RevokeCurrentAsync(context, AdminAuthSchemes.Pending).ConfigureAwait(false);

            var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
            if (!options.SingleLogoutEnabled)
            {
                return Results.NotFound();
            }

            var configuration = options.ToSaml2Configuration();
            var httpRequest = context.Request.ToGenericHttpRequest(validate: true);

            if (IsLogoutResponse(context))
            {
                var relay = ReadRelay(context, protectionProvider, AdminEntry(context, adminPath));
                ClearRelay(context);

                try
                {
                    var response = new Saml2LogoutResponse(configuration);
                    httpRequest.Binding.Unbind(httpRequest, response);

                    // **こちらが出した要求への応答であることを確かめる**
                    if (relay is null
                        || !string.Equals(
                            response.InResponseToAsString, relay.RequestId, StringComparison.Ordinal))
                    {
                        logger.LogWarning(
                            "単一ログアウトの応答が、こちらの出した要求のものではありませんでした。");
                    }
                }
                catch (Exception exception) when (IsSamlFailure(exception))
                {
                    logger.LogWarning(exception, "単一ログアウトの応答を受け取れませんでした。");
                }

                // **どちらにせよ、こちらは落ちている**
                return Results.Redirect(AdminEntry(context, adminPath));
            }

            try
            {
                var request = new Saml2LogoutRequest(configuration);

                // **署名・発行者を確かめるのはここ**
                httpRequest.Binding.Unbind(httpRequest, request);

                var response = new Saml2LogoutResponse(configuration)
                {
                    InResponseTo = request.Id,
                    Status = Saml2StatusCodes.Success,
                    Destination = options.SingleLogoutUrl,
                };

                var binding = new Saml2RedirectBinding();
                binding.Bind(response);

                logger.LogInformation("IdP からの単一ログアウトを受け取りました。");

                return Results.Redirect(binding.RedirectLocation.OriginalString);
            }
            catch (Exception exception) when (IsSamlFailure(exception))
            {
                // **理由は外へ返さない**（ログインの受け口と同じ）
                logger.LogWarning(exception, "単一ログアウトの要求を受け取れませんでした。");
                return Results.Redirect(AdminEntry(context, adminPath));
            }
        };

        group.MapGet("/slo", slo).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);
        group.MapPost("/slo", slo).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);
    }

    /// <summary>特権管理者だけが使える SAML 設定の入口。</summary>
    private static void MapSettings(RouteGroupBuilder group)
    {
        var policy = AdminPermissions.PolicyOf(AdminPermissions.SamlSettings);

        group.MapGet("/settings", async (
            ISamlOptionsProvider provider,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(SettingsBody(snapshot));
        }).RequireAuthorization(policy);

        group.MapPut("/settings", async (
            SamlSettingsRequest request,
            HttpContext context,
            ISamlOptionsProvider provider,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var before = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
                var changedFields = ChangedFields(before, request);
                if (changedFields.Count > 0)
                {
                    // **証明書の本文は残さない。** どの項目を変えたかだけで改竄を追える。
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

        group.MapPost("/settings/test", async (
            SamlMetadataTestRequest request,
            IHttpClientFactory clientFactory,
            CancellationToken cancellationToken) =>
        {
            if (!Uri.TryCreate(request.MetadataUrl?.Trim(), UriKind.Absolute, out var metadataUrl)
                || (metadataUrl.Scheme != Uri.UriSchemeHttps
                    && metadataUrl.Scheme != Uri.UriSchemeHttp))
            {
                return Results.BadRequest(new { code = "invalid-url" });
            }

            try
            {
                using var response = await clientFactory
                    .CreateClient("SamlMetadata")
                    .GetAsync(metadataUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 1_048_576,
                    XmlResolver = null,
                });

                while (reader.Read() && reader.NodeType != XmlNodeType.Element)
                {
                }

                if (reader.LocalName is not ("EntityDescriptor" or "EntitiesDescriptor"))
                {
                    return Results.BadRequest(new { code = "not-metadata" });
                }

                return Results.Ok(new
                {
                    reachable = true,
                    entityId = reader.GetAttribute("entityID"),
                });
            }
            catch (Exception exception) when (exception is HttpRequestException
                or TaskCanceledException
                or XmlException)
            {
                return Results.BadRequest(new { code = "fetch-failed" });
            }
        }).RequireAuthorization(policy);
    }

    private static IReadOnlyList<string> ChangedFields(
        SamlOptionsSnapshot before,
        SamlSettingsRequest request)
    {
        var old = before.Values;
        var changed = new List<string>();

        void Add(string name, string key, string? previous, string? next)
        {
            if (!before.FixedKeys.Contains(key)
                && !string.Equals(previous?.Trim(), next?.Trim(), StringComparison.Ordinal))
            {
                changed.Add(name);
            }
        }

        Add("enabled", SamlOptions.EnabledKey, old.Enabled, request.Enabled ? "true" : "false");
        Add("entityId", SamlOptions.EntityIdKey, old.EntityId, request.EntityId);
        Add("idpEntityId", SamlOptions.IdpEntityIdKey, old.IdpEntityId, request.IdpEntityId);
        Add(
            "singleSignOnUrl",
            SamlOptions.SingleSignOnUrlKey,
            old.SingleSignOnUrl,
            request.SingleSignOnUrl);
        Add(
            "idpCertificate",
            SamlOptions.IdpCertificateKey,
            old.IdpCertificate,
            request.IdpCertificate);
        Add("unknownUser", SamlOptions.UnknownUserKey, old.UnknownUser, request.UnknownUser);
        Add("registerRole", SamlOptions.RegisterRoleKey, old.RegisterRole, request.RegisterRole);
        Add(
            "loginIdSource",
            SamlOptions.LoginIdSourceKey,
            old.LoginIdSource,
            request.LoginIdSource);
        Add("loginIdClaim", SamlOptions.LoginIdClaimKey, old.LoginIdClaim, request.LoginIdClaim);
        Add("buttonLabel", SamlOptions.ButtonLabelKey, old.ButtonLabel, request.ButtonLabel);
        Add(
            "singleLogoutUrl",
            SamlOptions.SingleLogoutUrlKey,
            old.SingleLogoutUrl,
            request.SingleLogoutUrl);

        return changed;
    }

    private static object SettingsBody(SamlOptionsSnapshot snapshot)
    {
        bool Fixed(string key) => snapshot.FixedKeys.Contains(key);
        var values = snapshot.Values;

        return new
        {
            enabled = string.Equals(values.Enabled, "true", StringComparison.OrdinalIgnoreCase),
            entityId = values.EntityId,
            idpEntityId = values.IdpEntityId,
            singleSignOnUrl = values.SingleSignOnUrl,
            idpCertificate = values.IdpCertificate,
            unknownUser = values.UnknownUser,
            registerRole = values.RegisterRole,
            loginIdSource = values.LoginIdSource,
            loginIdClaim = values.LoginIdClaim,
            buttonLabel = values.ButtonLabel,
            singleLogoutUrl = values.SingleLogoutUrl,
            fixedFields = new
            {
                enabled = Fixed(SamlOptions.EnabledKey),
                entityId = Fixed(SamlOptions.EntityIdKey),
                idpEntityId = Fixed(SamlOptions.IdpEntityIdKey),
                singleSignOnUrl = Fixed(SamlOptions.SingleSignOnUrlKey),
                idpCertificate = Fixed(SamlOptions.IdpCertificateKey),
                unknownUser = Fixed(SamlOptions.UnknownUserKey),
                registerRole = Fixed(SamlOptions.RegisterRoleKey),
                loginIdSource = Fixed(SamlOptions.LoginIdSourceKey),
                loginIdClaim = Fixed(SamlOptions.LoginIdClaimKey),
                buttonLabel = Fixed(SamlOptions.ButtonLabelKey),
                singleLogoutUrl = Fixed(SamlOptions.SingleLogoutUrlKey),
            },
        };
    }

    public sealed record SamlSettingsRequest(
        bool Enabled,
        string? EntityId,
        string? IdpEntityId,
        string? SingleSignOnUrl,
        string? IdpCertificate,
        string? UnknownUser,
        string? RegisterRole,
        string? LoginIdSource,
        string? LoginIdClaim,
        string? ButtonLabel,
        string? SingleLogoutUrl)
    {
        public SamlSettingValues ToValues() => new()
        {
            Enabled = Enabled ? "true" : "false",
            EntityId = EntityId,
            IdpEntityId = IdpEntityId,
            SingleSignOnUrl = SingleSignOnUrl,
            IdpCertificate = IdpCertificate,
            UnknownUser = UnknownUser,
            RegisterRole = RegisterRole,
            LoginIdSource = LoginIdSource,
            LoginIdClaim = LoginIdClaim,
            ButtonLabel = ButtonLabel,
            SingleLogoutUrl = SingleLogoutUrl,
        };
    }

    public sealed record SamlMetadataTestRequest(string? MetadataUrl);

    /// <summary>来たものが「頼んだぶんの返事」か。</summary>
    private static bool IsLogoutResponse(HttpContext context)
    {
        if (context.Request.Query.ContainsKey("SAMLResponse"))
        {
            return true;
        }

        return context.Request.HasFormContentType
            && context.Request.Form.ContainsKey("SAMLResponse");
    }

    /// <summary>SAML のやり取りで「受け取れなかった」を表す失敗か。</summary>
    private static bool IsSamlFailure(Exception exception) =>
        exception is Saml2RequestException
            or Saml2BindingException
            or InvalidSignatureException
            or InvalidOperationException
            or ArgumentException
            or FormatException;

    /// <summary>単一ログアウトに要る手掛かりを取り出す（Issue #191）。</summary>
    /// <remarks>**<c>NameID</c> が無ければ頼めない。** その場合は <c>null</c>。</remarks>
    private static SamlSessionKeys? ReadSessionKeys(ClaimsIdentity? identity)
    {
        if (identity is null)
        {
            return null;
        }

        var nameId = identity.FindFirst(AdminAuthSchemes.SamlNameIdClaim)?.Value
            ?? identity.FindFirst(Saml2ClaimTypes.NameId)?.Value
            ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(nameId))
        {
            return null;
        }

        var sessionIndex = identity.FindFirst(AdminAuthSchemes.SamlSessionIndexClaim)?.Value
            ?? identity.FindFirst(Saml2ClaimTypes.SessionIndex)?.Value;

        return new SamlSessionKeys(nameId, sessionIndex);
    }

    /// <summary>SAML で入ったときに、セッションへ預ける値（単一ログアウトの手掛かり）。無ければ <c>null</c>。</summary>
    private static SessionSso? SsoOf(ClaimsIdentity? identity) =>
        ReadSessionKeys(identity) is { } keys ? new SessionSso(Saml: keys) : null;

    /// <summary>管理画面の入口を、配置先のサブパス込みで返す（Issue #465）。</summary>
    /// <remarks>
    /// ⚠️ **リダイレクト先はサブパスを自動では含まない。** 素の管理画面パスへ返すと、
    /// サブパス配下に置いたときに同じホストの別アプリ（Pleasanter）へ飛んでしまう。
    /// 画面から届く <c>returnUrl</c> もサブパス込みなので、照合もこの値で行う。
    /// </remarks>
    private static string AdminEntry(HttpContext context, string adminPath) =>
        context.Request.PathBase.Add(adminPath).ToUriComponent();

    /// <summary>サブパス込みの SAML の経路。**cookie の Path に使う。**</summary>
    private static string SamlCookiePath(HttpContext context) =>
        context.Request.PathBase.Add("/api/admin/saml").ToUriComponent();

    /// <summary>失敗の理由を監査ログへ残して、管理画面へ戻す（Issue #602）。</summary>
    /// <remarks>
    /// **応答は 302 のままだが、監査ログには失敗として残す**（<see cref="AuditNotes.SetStatus"/>）。
    /// 理由は画面へは返さず（<see cref="SamlFailure"/>）、管理者が読む記録にだけ書く。
    /// </remarks>
    private static IResult FailedAudited(
        HttpContext context,
        string adminPath,
        string failure,
        string? reason = null,
        int statusCode = StatusCodes.Status401Unauthorized)
    {
        AuditNotes.Add(context, "result", failure);
        if (reason is not null)
        {
            AuditNotes.Add(context, "reason", reason);
        }

        AuditNotes.SetStatus(context, statusCode);
        return Failed(adminPath, failure);
    }

    /// <summary>失敗の印を付けて管理画面へ戻す。</summary>
    private static IResult Failed(string adminPath, string failure) =>
        Results.Redirect($"{adminPath}?samlError={failure}");

    /// <summary>画面へ返す失敗の印。**詳しい理由は返さない。**</summary>
    private static class SamlFailure
    {
        /// <summary>応答が受け取れなかった（署名・宛先・期限・対応する要求が無い）。</summary>
        public const string Invalid = "invalid";
    }

    /// <summary>この要求の配信元から受け口の URL を作る。</summary>
    /// <remarks>
    /// **設定へ書かせない。** 逆プロキシの前後で食い違ったときに気付けなくなる。
    /// 転送ヘッダの扱いは <c>UseForwardedHeaders</c> に任せる。
    /// **サブパス（PathBase）も含める**（Issue #465）。
    /// </remarks>
    private static Uri AssertionConsumerServiceUrl(HttpContext context) =>
        new(new Uri($"{context.Request.Scheme}://{context.Request.Host.ToUriComponent()}"
            + $"{context.Request.PathBase.ToUriComponent()}/"),
            "api/admin/saml/acs");

    /// <summary>ログイン ID として使う値を取り出す。</summary>
    private static string? ReadLoginId(ClaimsIdentity? identity, SamlOptions options)
    {
        if (identity is null)
        {
            return null;
        }

        if (options.LoginIdSource == SamlLoginIdSource.Claim)
        {
            return identity.FindFirst(options.LoginIdClaim)?.Value;
        }

        // **NameID は独自の claim 型で入る。** 見つからないときは標準の型も見る
        return identity.FindFirst(Saml2ClaimTypes.NameId)?.Value
            ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>戻り先を自サイト内だけに絞る。</summary>
    /// <remarks>
    /// ⚠️ **他所へ飛ばせると、ログインの流れを踏み台にした誘導ができる。**
    /// <c>//example.com</c> のような書き方も外す。
    /// </remarks>
    private static string LocalReturnUrl(string? returnUrl, string adminPath)
    {
        var isAdminPath = string.Equals(returnUrl, adminPath, StringComparison.Ordinal)
            || returnUrl?.StartsWith($"{adminPath}/", StringComparison.Ordinal) is true
            || returnUrl?.StartsWith($"{adminPath}?", StringComparison.Ordinal) is true;
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            || !isAdminPath)
        {
            // **管理画面の中だけを許す。** 回答画面へ戻す意味は無い
            return adminPath;
        }

        return returnUrl;
    }

    /// <summary>やり取りの途中。</summary>
    private sealed record Relay(string RequestId, string ReturnUrl);

    private static IDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector(ProtectorPurpose);

    private static void SaveRelay(
        HttpContext context,
        IDataProtectionProvider provider,
        string requestId,
        string returnUrl)
    {
        // **中身を読ませない・書き換えさせない。** Data Protection で包む
        var payload = Protector(provider).Protect($"{requestId}\n{returnUrl}");

        context.Response.Cookies.Append(RelayCookieName, payload, new CookieOptions
        {
            HttpOnly = true,

            // **IdP からの他サイト POST で戻ってくる必要がある**
            SameSite = SameSiteMode.None,
            Secure = true,
            IsEssential = true,
            // **サブパスの中に閉じる。** 同じホストの Pleasanter へ送らない（Issue #465）
            Path = SamlCookiePath(context),
            MaxAge = RelayLifetime,
        });
    }

    private static Relay? ReadRelay(
        HttpContext context,
        IDataProtectionProvider provider,
        string adminPath)
    {
        if (!context.Request.Cookies.TryGetValue(RelayCookieName, out var payload)
            || string.IsNullOrEmpty(payload))
        {
            return null;
        }

        string unprotected;
        try
        {
            unprotected = Protector(provider).Unprotect(payload);
        }
        catch (Exception exception) when (exception is FormatException
            or System.Security.Cryptography.CryptographicException)
        {
            // **包みが壊れているものは無かったものとして扱う**
            return null;
        }

        var parts = unprotected.Split('\n', 2);
        return parts.Length == 2 && parts[0].Length > 0
            ? new Relay(parts[0], LocalReturnUrl(parts[1], adminPath))
            : null;
    }

    private static void ClearRelay(HttpContext context) =>
        context.Response.Cookies.Delete(RelayCookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.None,
            Secure = true,
            // **サブパスの中に閉じる。** 同じホストの Pleasanter へ送らない（Issue #465）
            Path = SamlCookiePath(context),
        });
}
