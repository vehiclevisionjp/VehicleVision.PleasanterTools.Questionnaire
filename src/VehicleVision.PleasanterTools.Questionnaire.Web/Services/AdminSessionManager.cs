using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者セッションの内容を保護し、cookie には識別子だけを渡す。</summary>
public sealed class AdminSessionManager
{
    /// <summary>cookie と復元後の principal に載せるセッション ID。</summary>
    public const string SessionIdClaim = "questionnaire:admin_session_id";

    private const string ProtectionPurpose = "Questionnaire.AdminSession.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAdminSessionStore _store;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminSessionManager> _logger;

    public AdminSessionManager(
        IAdminSessionStore store,
        IDataProtectionProvider protectionProvider,
        TimeProvider time,
        ILogger<AdminSessionManager> logger)
    {
        _store = store;
        _protector = protectionProvider.CreateProtector(ProtectionPurpose);
        _time = time;
        _logger = logger;
    }

    /// <summary>同じ方式の古い状態を捨てて、新しいセッションを発行する。</summary>
    public async Task<Guid> SignInAsync(
        HttpContext context,
        string scheme,
        AdminSessionKind kind,
        ClaimsPrincipal principal,
        TimeSpan lifetime)
    {
        await RevokeCurrentAsync(context, scheme).ConfigureAwait(false);

        var sessionId = Guid.NewGuid();
        var now = _time.GetUtcNow().UtcDateTime;
        var payload = new SessionPayload(
            principal.Claims.Select(claim => new StoredClaim(claim.Type, claim.Value)).ToArray(),
            principal.Identity?.AuthenticationType ?? scheme);

        await _store.CreateAsync(
            new AdminSessionEntry
            {
                AdminSessionId = sessionId,
                AdminUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
                Kind = kind,
                ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload, JsonOptions)),
                CreatedAt = now,
                ExpiresAt = now.Add(lifetime),
                IpAddress = IpAddressOf(context),
                UserAgent = Trim(context.Request.Headers.UserAgent.ToString(), 512),
            },
            context.RequestAborted).ConfigureAwait(false);

        var cookieIdentity = new ClaimsIdentity(
            [new Claim(SessionIdClaim, sessionId.ToString())],
            scheme);
        await context.SignInAsync(
            scheme,
            new ClaimsPrincipal(cookieIdentity),
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>ストアに残っている内容から principal を復元する。</summary>
    public async Task<(AdminSessionEntry Entry, ClaimsPrincipal Principal)?> FindAsync(
        ClaimsPrincipal? cookiePrincipal,
        AdminSessionKind expectedKind,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(cookiePrincipal?.FindFirstValue(SessionIdClaim), out var sessionId))
        {
            return null;
        }

        var entry = await _store.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        if (entry is null || entry.Kind != expectedKind)
        {
            return null;
        }

        if (AsUtc(entry.ExpiresAt) <= now)
        {
            await _store.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            var json = _protector.Unprotect(entry.ProtectedPayload);
            var payload = JsonSerializer.Deserialize<SessionPayload>(json, JsonOptions)
                ?? throw new JsonException("セッションの内容が空だった。");
            var claims = payload.Claims
                .Select(claim => new Claim(claim.Type, claim.Value))
                .Append(new Claim(SessionIdClaim, sessionId.ToString()));
            var identity = new ClaimsIdentity(claims, payload.AuthenticationType);
            return (entry, new ClaimsPrincipal(identity));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            _logger.LogError(exception, "管理者セッション {SessionId} の内容を復元できなかった", sessionId);
            await _store.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>指定した認証方式の現在のセッションをストアと cookie の両方から消す。</summary>
    public async Task RevokeCurrentAsync(HttpContext context, string scheme)
    {
        var result = await context.AuthenticateAsync(scheme).ConfigureAwait(false);
        if (Guid.TryParse(result.Principal?.FindFirstValue(SessionIdClaim), out var sessionId))
        {
            await _store.DeleteAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
        }

        await context.SignOutAsync(scheme).ConfigureAwait(false);
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind is DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

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

    private sealed record StoredClaim(string Type, string Value);

    private sealed record SessionPayload(
        IReadOnlyList<StoredClaim> Claims,
        string AuthenticationType);
}
