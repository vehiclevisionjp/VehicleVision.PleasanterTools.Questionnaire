using System.Security.Claims;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.Cryptography;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.DataProtection;
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
/// **この入口は、SAML を有効にしたときだけ生える**（<c>Program.cs</c>）。
/// ここで「無効なら 404」と書き分けていない。**経路そのものを作らない方が強い。**
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

    /// <summary>ログインの後に戻る既定の場所。</summary>
    private const string DefaultReturnUrl = "/admin";

    /// <summary>データの保護に使う用途の名前。</summary>
    private const string ProtectorPurpose = "VehicleVision.Questionnaire.Saml.RelayState";

    public static IEndpointRouteBuilder MapAdminSamlEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var group = builder.MapGroup("/api/admin/saml");
        AdminAuthSchemes.AddNoStore(group);

        // **入れた・断られたを記録に残す**（Issue #19）。GET は残らない
        group.AddEndpointFilter<AuditLogFilter>();

        // ---- SP のメタデータ -------------------------------------------------
        // **IdP へ登録する値を、実際に動いている設定から出す。**
        // 手で書き写すと、EntityID や受け口の URL の食い違いに気付けない
        group.MapGet("/metadata", (HttpContext context, SamlOptions options) =>
        {
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
        group.MapGet("/login", (
            HttpContext context,
            SamlOptions options,
            IDataProtectionProvider protectionProvider,
            string? returnUrl) =>
        {
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
                LocalReturnUrl(returnUrl));

            return Results.Redirect(binding.RedirectLocation.OriginalString);
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        // ---- IdP から受け取る ------------------------------------------------
        group.MapPost("/acs", async (
            HttpContext context,
            SamlOptions options,
            SamlAuthenticator authenticator,
            IDataProtectionProvider protectionProvider,
            ILogger<SamlAuthenticator> logger,
            CancellationToken cancellationToken) =>
        {
            var relay = ReadRelay(context, protectionProvider);
            ClearRelay(context);

            if (relay is null)
            {
                // **こちらが出した要求と結び付かない応答は受け取らない**
                logger.LogWarning("SAML の応答に、対応する要求が見つかりませんでした。");
                return Failed(SamlFailure.Invalid);
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
                    return Failed(SamlFailure.Invalid);
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
                return Failed(SamlFailure.Invalid);
            }

            // **出した要求への応答であることを確かめる**
            if (!string.Equals(response.InResponseToAsString, relay.RequestId, StringComparison.Ordinal))
            {
                logger.LogWarning("SAML の応答が、こちらの出した要求のものではありませんでした。");
                return Failed(SamlFailure.Invalid);
            }

            var loginId = ReadLoginId(response.ClaimsIdentity, options);
            if (string.IsNullOrWhiteSpace(loginId))
            {
                logger.LogWarning("SAML の応答にログイン ID として使える値がありませんでした。");
                return Failed(SamlFailure.Invalid);
            }

            // **誰が来たかを記録に残す**（AuditNotes は本文へ触らない）
            AuditNotes.Add(context, "loginId", loginId);

            var result = await authenticator.SignInAsync(loginId, cancellationToken)
                .ConfigureAwait(false);

            if (result.Registered)
            {
                AuditNotes.Add(context, "samlRegistered", "true");
            }

            switch (result.Outcome)
            {
                case SamlSignInOutcome.SignedIn:
                    await AdminAuthEndpoints.SignInSessionAsync(context, result.User!)
                        .ConfigureAwait(false);
                    return Results.Redirect(relay.ReturnUrl);

                case SamlSignInOutcome.NeedsSecondFactor:
                case SamlSignInOutcome.NeedsTotpEnrollment:
                    // **2 要素を登録済みなら通し、必須なら登録させる**（Issue #154）。
                    // どちらも「途中状態」で管理画面へ戻し、画面が続きを出す
                    await AdminAuthEndpoints.SignInPendingAsync(context, result.User!, secret: null)
                        .ConfigureAwait(false);
                    return Results.Redirect(relay.ReturnUrl);

                case SamlSignInOutcome.Disabled:
                    return Failed(SamlFailure.Disabled);

                default:
                    return Failed(SamlFailure.UnknownUser);
            }
        }).RequireRateLimiting(AdminAuthSchemes.LoginRateLimitPolicy);

        return builder;
    }

    /// <summary>失敗の印を付けて管理画面へ戻す。</summary>
    private static IResult Failed(string failure) =>
        Results.Redirect($"{DefaultReturnUrl}?samlError={failure}");

    /// <summary>画面へ返す失敗の印。**詳しい理由は返さない。**</summary>
    private static class SamlFailure
    {
        /// <summary>応答が受け取れなかった（署名・宛先・期限・対応する要求が無い）。</summary>
        public const string Invalid = "invalid";

        /// <summary>本アプリに居ない利用者だった。</summary>
        public const string UnknownUser = "unknown-user";

        /// <summary>止められている利用者だった。</summary>
        public const string Disabled = "disabled";
    }

    /// <summary>この要求の配信元から受け口の URL を作る。</summary>
    /// <remarks>
    /// **設定へ書かせない。** 逆プロキシの前後で食い違ったときに気付けなくなる。
    /// 転送ヘッダの扱いは <c>UseForwardedHeaders</c> に任せる。
    /// </remarks>
    private static Uri AssertionConsumerServiceUrl(HttpContext context) =>
        new(new Uri($"{context.Request.Scheme}://{context.Request.Host.ToUriComponent()}/"),
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
    private static string LocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            || !returnUrl.StartsWith(DefaultReturnUrl, StringComparison.Ordinal))
        {
            // **管理画面の中だけを許す。** 回答画面へ戻す意味は無い
            return DefaultReturnUrl;
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
            Path = "/api/admin/saml",
            MaxAge = RelayLifetime,
        });
    }

    private static Relay? ReadRelay(HttpContext context, IDataProtectionProvider provider)
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
            ? new Relay(parts[0], LocalReturnUrl(parts[1]))
            : null;
    }

    private static void ClearRelay(HttpContext context) =>
        context.Response.Cookies.Delete(RelayCookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.None,
            Secure = true,
            Path = "/api/admin/saml",
        });
}
