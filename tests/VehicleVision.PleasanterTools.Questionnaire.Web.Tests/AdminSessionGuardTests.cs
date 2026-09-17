using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理者セッションを要求ごとに見直す。</summary>
public class AdminSessionGuardTests
{
    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        public List<string?> SignedOutSchemes { get; } = [];
        public ClaimsPrincipal? SignedInPrincipal { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties)
        {
            SignedInPrincipal = principal;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutSchemes.Add(scheme);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdminSessionStore : IAdminSessionStore
    {
        private readonly Dictionary<Guid, AdminSessionEntry> _entries = [];

        public Task CreateAsync(AdminSessionEntry entry, CancellationToken cancellationToken = default)
        {
            _entries.Add(entry.AdminSessionId, entry);
            return Task.CompletedTask;
        }

        public Task<AdminSessionEntry?> FindAsync(
            Guid adminSessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(adminSessionId));

        public Task<IReadOnlyList<AdminSessionEntry>> ListAsync(
            Guid adminUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdminSessionEntry>>(
                _entries.Values.Where(entry => entry.AdminUserId == adminUserId).ToList());

        public Task<bool> DeleteAsync(
            Guid adminSessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.Remove(adminSessionId));

        public Task<IReadOnlyList<Guid>> DeleteAllExceptAsync(
            Guid adminUserId,
            Guid? exceptSessionId,
            CancellationToken cancellationToken = default)
        {
            var ids = _entries.Values
                .Where(entry => entry.AdminUserId == adminUserId
                    && (entry.AdminSessionId != exceptSessionId
                        || entry.Kind is not AdminSessionKind.Session))
                .Select(entry => entry.AdminSessionId)
                .ToList();
            ids.ForEach(id => _entries.Remove(id));
            return Task.FromResult<IReadOnlyList<Guid>>(ids);
        }

        public Task<int> DeleteExpiredAsync(
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            var ids = _entries.Values
                .Where(entry => entry.ExpiresAt <= nowUtc)
                .Select(entry => entry.AdminSessionId)
                .ToList();
            ids.ForEach(id => _entries.Remove(id));
            return Task.FromResult(ids.Count);
        }
    }

    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AdminUser User(
        Guid? adminUserId = null,
        AdminRole role = AdminRole.Administrator,
        bool isDisabled = false) => new()
        {
            AdminUserId = adminUserId ?? AdminUserId,
            LoginId = "admin",
            PasswordHash = "hash",
            Role = role,
            IsDisabled = isDisabled,
            CreatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
        };

    private static ClaimsPrincipal Principal(Guid? adminUserId = null, string? role = "Administrator")
    {
        var claims = new List<Claim>();
        if (adminUserId is { } id)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
        }

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AdminAuthSchemes.Session));
    }

    private static async Task<(CookieValidatePrincipalContext Context, FakeAuthenticationService Auth)> ValidateAsync(
        AdminUser? user,
        ClaimsPrincipal principal)
    {
        var time = TimeProvider.System;
        var store = new FakeAdminUserStore(time);
        if (user is not null)
        {
            await store.CreateAsync(user);
        }

        var auth = new FakeAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAdminUserStore>(store)
            .AddSingleton<IAdminSessionStore, FakeAdminSessionStore>()
            .AddSingleton<IAuthenticationService>(auth)
            .AddSingleton(TimeProvider.System)
            .AddDataProtection()
            .Services
            .AddLogging()
            .AddSingleton<AdminSessionManager>()
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
        };
        ClaimsPrincipal cookiePrincipal = principal;
        if (principal.FindFirstValue(ClaimTypes.NameIdentifier) is not null)
        {
            await services.GetRequiredService<AdminSessionManager>().SignInAsync(
                httpContext,
                AdminAuthSchemes.Session,
                AdminSessionKind.Session,
                principal,
                AdminAuthSchemes.SessionLifetime);
            cookiePrincipal = auth.SignedInPrincipal!;
            auth.SignedOutSchemes.Clear();
        }

        var scheme = new AuthenticationScheme(
            AdminAuthSchemes.Session,
            AdminAuthSchemes.Session,
            typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(
            cookiePrincipal,
            new AuthenticationProperties(),
            AdminAuthSchemes.Session);
        var context = new CookieValidatePrincipalContext(
            httpContext,
            scheme,
            new CookieAuthenticationOptions(),
            ticket);

        await AdminSessionGuard.ValidateAsync(context);
        return (context, auth);
    }

    [Fact]
    public async Task 有効な管理者で役割が一致すればセッションを残す()
    {
        var (context, auth) = await ValidateAsync(User(), Principal(AdminUserId));

        Assert.False(context.ShouldRenew);
        Assert.NotNull(context.Principal);
        Assert.Empty(auth.SignedOutSchemes);
        Assert.Collection(
            auth.SignedInPrincipal!.Claims,
            claim => Assert.Equal(AdminSessionManager.SessionIdClaim, claim.Type));
    }

    [Fact]
    public async Task セッションに管理者IDが無ければ拒否してCookieを消す()
    {
        var (context, auth) = await ValidateAsync(User(), Principal(adminUserId: null));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, auth.SignedOutSchemes);
    }

    [Fact]
    public async Task DBに管理者が無ければ期限切れ扱いで拒否する()
    {
        var (context, auth) = await ValidateAsync(null, Principal(AdminUserId));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 止められた管理者は拒否する()
    {
        var (context, auth) = await ValidateAsync(User(isDisabled: true), Principal(AdminUserId));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 役割が降格されていれば古い管理者権限のCookieを拒否する()
    {
        var (context, auth) = await ValidateAsync(
            User(role: AdminRole.Editor),
            Principal(AdminUserId, role: "Administrator"));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 役割の大文字小文字が違うCookieは拒否する()
    {
        var (context, auth) = await ValidateAsync(
            User(role: AdminRole.Administrator),
            Principal(AdminUserId, role: "administrator"));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, auth.SignedOutSchemes);
    }
}
