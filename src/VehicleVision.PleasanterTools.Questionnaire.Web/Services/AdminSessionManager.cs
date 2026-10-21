using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>ストアから復元した管理者セッション。</summary>
/// <param name="SessionId">cookie に載せている ID（<b>秘密</b>。ログへ出さない）。</param>
/// <param name="Handle">端末を指す識別子（ID の SHA-256。ログ・一覧・監査へ出してよい）。</param>
/// <param name="Session">ストアの中身。</param>
/// <param name="User">持ち主。<b>居なければ <c>null</c></b>（呼び出し側が落とす）。</param>
/// <param name="Principal">復元した principal（利用者・方式・外部ログインの鍵から組み立て直したもの）。</param>
public sealed record RestoredAdminSession(
    string SessionId,
    string Handle,
    AdminSession Session,
    AdminUser? User,
    ClaimsPrincipal Principal);

/// <summary>管理者セッションをサーバ側（Core.Data のストア）に持ち、cookie には ID だけを渡す。</summary>
/// <remarks>
/// <para>
/// cookie の中身（暗号化したチケット）に載るのは<b>セッション ID だけ</b>。ストアには ID の SHA-256 だけが入る。
/// <b>利用者・役割・ログインの方式は、要求のたびにストアと利用者の表から組み立て直す</b>
/// （Core のセッションは、セッションごとの内容の保管を持たない）。
/// </para>
/// <para>
/// 役割は<b>いまの値</b>が入る。役割を変えたときに古い役割のセッションを残さないよう、
/// 変える側が持ち主のセッションを失効させる（<see cref="AdminUserService.SetRoleAsync"/>）。
/// </para>
/// </remarks>
public sealed class AdminSessionManager(
    IAdminSessionStore store,
    IAdminUserStore users,
    ILogger<AdminSessionManager> logger)
{
    /// <summary>cookie と復元後の principal に載せるセッション ID。</summary>
    public const string SessionIdClaim = "questionnaire:admin_session_id";

    /// <summary>同じ方式の古い状態を捨てて、新しいセッションを発行する。</summary>
    /// <param name="sso">
    /// SAML・Pleasanter のログインで入ったときの鍵（単一ログアウト・再検証に要る）。
    /// <b>2 要素の途中からログイン後へは、そのまま引き継ぐ。</b>
    /// </param>
    /// <returns>cookie に載せたセッション ID。</returns>
    public async Task<string> SignInAsync(
        HttpContext context,
        string scheme,
        string kind,
        Guid userId,
        TimeSpan lifetime,
        SessionSso? sso = null)
    {
        await RevokeCurrentAsync(context, scheme).ConfigureAwait(false);

        var sessionId = await store.CreateAsync(
            userId,
            kind,
            lifetime,
            context.RequestAborted,
            IpAddressOf(context),
            Trim(context.Request.Headers.UserAgent.ToString(), 512),
            sso).ConfigureAwait(false);

        var cookieIdentity = new ClaimsIdentity(
            [new Claim(SessionIdClaim, sessionId)],
            scheme);
        await context.SignInAsync(
            scheme,
            new ClaimsPrincipal(cookieIdentity),
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>ストアに残っている内容から principal を復元する。</summary>
    /// <param name="authenticationType">principal の認証の種別（認証方式の名前）。</param>
    public async Task<RestoredAdminSession?> FindAsync(
        ClaimsPrincipal? cookiePrincipal,
        string expectedKind,
        string authenticationType,
        CancellationToken cancellationToken)
    {
        var sessionId = cookiePrincipal?.FindFirstValue(SessionIdClaim);
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        // 期限切れは、ストアが「無い」として返す
        var session = await store.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.Kind != expectedKind)
        {
            return null;
        }

        var user = await users.FindByIdAsync(session.UserId, cancellationToken).ConfigureAwait(false);
        var sso = await store.GetSsoAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var handle = store.HandleOf(sessionId);

        if (user is null)
        {
            logger.LogWarning("管理者セッション {Handle} の持ち主が居ない（AdminUserId={AdminUserId}）", handle, session.UserId);
            return new RestoredAdminSession(sessionId, handle, session, null, new ClaimsPrincipal());
        }

        return new RestoredAdminSession(
            sessionId,
            handle,
            session,
            user,
            new ClaimsPrincipal(new ClaimsIdentity(ClaimsOf(user, sso, sessionId), authenticationType)));
    }

    /// <summary>指定した認証方式の現在のセッションをストアと cookie の両方から消す。</summary>
    public async Task RevokeCurrentAsync(HttpContext context, string scheme)
    {
        var result = await context.AuthenticateAsync(scheme).ConfigureAwait(false);
        if (result.Principal?.FindFirstValue(SessionIdClaim) is { Length: > 0 } sessionId)
        {
            await store.DeleteAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        }

        await context.SignOutAsync(scheme).ConfigureAwait(false);
    }

    private static List<Claim> ClaimsOf(AdminUser user, SessionSso? sso, string sessionId)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.LoginId),
            new(ClaimTypes.Role, user.Role),
            new(SessionIdClaim, sessionId),
        ];

        // **IdP へログアウトを頼むのに要る値だけ**（Issue #191）
        if (sso?.Saml is { } saml)
        {
            claims.Add(new Claim(AdminAuthSchemes.SamlNameIdClaim, saml.NameId));
            if (!string.IsNullOrEmpty(saml.SessionIndex))
            {
                claims.Add(new Claim(AdminAuthSchemes.SamlSessionIndexClaim, saml.SessionIndex));
            }
        }

        if (sso?.Pleasanter is { } pleasanter)
        {
            claims.AddRange(PleasanterSsoClaims.Create(pleasanter));
        }

        return claims;
    }

    private static string? IpAddressOf(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null
            ? null
            : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    private static string? Trim(string? value, int length) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= length ? value : value[..length];
}
