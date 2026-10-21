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

    /// <summary>要求の打ち切りを真似るだけのセッションストア（Issue #426）。</summary>
    /// <remarks>**どの口も中断で返す。** 見たいのは「例外が外へ出ないこと」だけ。</remarks>
    private sealed class CancellingAdminSessionStore : IAdminSessionStore
    {
        private static readonly CancellationToken Canceled = new(canceled: true);

        // **作成は通す。** ログインを済ませないと、読み取りまで辿り着かない
        public Task<string> CreateAsync(
            Guid userId,
            string kind,
            TimeSpan lifetime,
            CancellationToken cancellationToken = default,
            string? ipAddress = null,
            string? userAgent = null,
            SessionSso? sso = null) =>
            Task.FromResult("session-id");

        public string HandleOf(string sessionId) => sessionId;

        public Task<SessionSso?> GetSsoAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<SessionSso?>(Canceled);

        public Task TouchPleasanterVerifiedAsync(
            string sessionId,
            DateTimeOffset verifiedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled(Canceled);

        public Task<SamlSessionKeys?> GetSamlKeysAsync(
            string sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<SamlSessionKeys?>(Canceled);

        public Task<int> DeleteBySamlKeysAsync(
            string nameId,
            string? sessionIndex,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<int>(Canceled);

        public Task<IReadOnlyList<AdminSessionInfo>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<IReadOnlyList<AdminSessionInfo>>(Canceled);

        public Task<bool> DeleteByHandleAsync(
            Guid userId,
            string handle,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<bool>(Canceled);

        public Task<AdminSession?> FindAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<AdminSession?>(Canceled);

        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromCanceled(Canceled);

        public Task DeleteForUserAsync(
            Guid userId,
            string? exceptSessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled(Canceled);

        public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default) =>
            Task.FromCanceled<int>(Canceled);
    }

    private static readonly Guid AdminUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    /// <summary>1 回の検証に使う部品。</summary>
    private sealed record Harness(
        FakeAdminUserStore Users,
        FakeAdminSessionStore Sessions,
        FakeAuthenticationService Auth,
        ServiceProvider Services,
        DefaultHttpContext HttpContext,
        ClaimsPrincipal CookiePrincipal);

    /// <summary>
    /// 管理者（<paramref name="role"/> など）を作り、そのセッションを張って、cookie の principal を取り出す。
    /// <paramref name="seedUser"/> が偽なら、管理者の表には何も作らない（セッションだけが残っている状態）。
    /// </summary>
    private static async Task<Harness> SignInAsync(
        bool seedUser = true,
        string role = nameof(AdminRole.Administrator),
        bool isDisabled = false,
        string kind = AdminSessionKinds.Session,
        string scheme = AdminAuthSchemes.Session,
        SessionSso? sso = null,
        Action<IServiceCollection>? configure = null)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var users = new FakeAdminUserStore(time);
        if (seedUser)
        {
            users.Seed(AdminUserId, "admin", role, isDisabled);
        }

        var sessions = new FakeAdminSessionStore(time);
        var auth = new FakeAuthenticationService();
        var collection = new ServiceCollection()
            .AddSingleton<IAdminUserStore>(users)
            .AddSingleton<IAdminSessionStore>(sessions)
            .AddSingleton<IAuthenticationService>(auth)
            .AddSingleton<TimeProvider>(time)
            .AddLogging()
            .AddSingleton<AdminSessionManager>();
        configure?.Invoke(collection);
        var services = collection.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        await services.GetRequiredService<AdminSessionManager>().SignInAsync(
            httpContext,
            scheme,
            kind,
            AdminUserId,
            AdminAuthSchemes.SessionLifetime,
            sso);
        var cookiePrincipal = auth.SignedInPrincipal!;
        auth.SignedOutSchemes.Clear();

        return new Harness(users, sessions, auth, services, httpContext, cookiePrincipal);
    }

    private static CookieValidatePrincipalContext ContextOf(
        HttpContext httpContext,
        ClaimsPrincipal cookiePrincipal,
        string scheme = AdminAuthSchemes.Session) =>
        new(
            httpContext,
            new AuthenticationScheme(scheme, scheme, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(cookiePrincipal, new AuthenticationProperties(), scheme));

    private static async Task<(CookieValidatePrincipalContext Context, Harness Harness)> ValidateAsync(
        Harness harness)
    {
        var context = ContextOf(harness.HttpContext, harness.CookiePrincipal);
        await AdminSessionGuard.ValidateAsync(context);
        return (context, harness);
    }

    /// <summary>
    /// ⚠️ **要求が打ち切られただけで例外を外へ出さない**（Issue #426）。
    /// 最外周まで飛ぶとデバッガが毎回止まり、本物の異常と見分けがつかなくなる。
    /// </summary>
    [Fact]
    public async Task 要求が打ち切られても例外を外へ出さずCookieも捨てない()
    {
        var auth = new FakeAuthenticationService();
        var services = new ServiceCollection()
            .AddSingleton<IAdminUserStore>(new FakeAdminUserStore(TimeProvider.System))
            .AddSingleton<IAdminSessionStore, CancellingAdminSessionStore>()
            .AddSingleton<IAuthenticationService>(auth)
            .AddSingleton(TimeProvider.System)
            .AddLogging()
            .AddSingleton<AdminSessionManager>()
            .BuildServiceProvider();

        // **先にログインさせる。** セッション ID が無いと、ストアに触れる前に拒否される
        var signInContext = new DefaultHttpContext { RequestServices = services };
        await services.GetRequiredService<AdminSessionManager>().SignInAsync(
            signInContext,
            AdminAuthSchemes.Session,
            AdminSessionKinds.Session,
            AdminUserId,
            AdminAuthSchemes.SessionLifetime);
        var cookiePrincipal = auth.SignedInPrincipal!;
        auth.SignedOutSchemes.Clear();

        // **打ち切られた要求を作る。** これが立っているときだけ素通りさせる決まり
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = aborted.Token,
        };

        var context = ContextOf(httpContext, cookiePrincipal);

        // 例外が外へ出れば、ここで落ちる
        await AdminSessionGuard.ValidateAsync(context);

        // ⚠️ **cookie を捨てないこと。** 中断を「セッション無効」に倒さない
        Assert.Empty(auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 有効な管理者ならセッションを残し利用者から組み立てた情報を載せる()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync());

        Assert.False(context.ShouldRenew);
        Assert.NotNull(context.Principal);
        Assert.Empty(harness.Auth.SignedOutSchemes);

        // cookie に載るのはセッション ID だけ
        Assert.Collection(
            harness.Auth.SignedInPrincipal!.Claims,
            claim => Assert.Equal(AdminSessionManager.SessionIdClaim, claim.Type));

        // **利用者・役割は、ストアと利用者の表から組み立て直す**（cookie の値を信じない）
        Assert.Equal(AdminUserId.ToString(), context.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("admin", context.Principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal(nameof(AdminRole.Administrator), context.Principal.FindFirstValue(ClaimTypes.Role));
        Assert.Equal(AdminAuthSchemes.Session, context.Principal.Identity!.AuthenticationType);
    }

    [Fact]
    public async Task セッションIDを持たないCookieは拒否してCookieを消す()
    {
        var harness = await SignInAsync();
        var noSession = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, AdminUserId.ToString())],
            AdminAuthSchemes.Session));
        var context = ContextOf(harness.HttpContext, noSession);

        await AdminSessionGuard.ValidateAsync(context);

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
    }

    [Fact]
    public async Task ストアに無いセッションIDは拒否する()
    {
        var harness = await SignInAsync();

        // **別の ID の cookie**（ストアに無い・失効した）
        var other = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminSessionManager.SessionIdClaim, "no-such-session")],
            AdminAuthSchemes.Session));
        var context = ContextOf(harness.HttpContext, other);

        await AdminSessionGuard.ValidateAsync(context);

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 失効させたセッションは通らない()
    {
        var harness = await SignInAsync();

        // **パスワード変更・役割の変更などで、持ち主のセッションを失効させる**
        await harness.Sessions.DeleteForUserAsync(AdminUserId);
        var (context, _) = await ValidateAsync(harness);

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
    }

    [Fact]
    public async Task 期限が切れたセッションは通らない()
    {
        var harness = await SignInAsync();
        var time = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)harness.Services.GetRequiredService<TimeProvider>();
        time.Advance(AdminAuthSchemes.SessionLifetime + TimeSpan.FromMinutes(1));

        var (context, _) = await ValidateAsync(harness);

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
    }

    [Fact]
    public async Task DBに管理者が無ければ拒否してセッションも消す()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync(seedUser: false));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
        Assert.Equal(0, harness.Sessions.Count);
    }

    [Fact]
    public async Task 止められた管理者は拒否してセッションも消す()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync(isDisabled: true));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
        Assert.Equal(0, harness.Sessions.Count);
    }

    [Fact]
    public async Task 役割は要求のたびに利用者の表から読み直す()
    {
        var harness = await SignInAsync();

        // **cookie に役割は載っていない。** 変えた後の役割が、次の要求からそのまま効く
        await harness.Users.SetRoleAsync(AdminUserId, nameof(AdminRole.Editor));
        var (context, _) = await ValidateAsync(harness);

        Assert.Equal(nameof(AdminRole.Editor), context.Principal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task 段階が違うセッションは通らない()
    {
        // **途中状態（Pending）の cookie を、ログイン済み（Session）として使えない**
        var harness = await SignInAsync(kind: AdminSessionKinds.Pending, scheme: AdminAuthSchemes.Pending);
        var (context, _) = await ValidateAsync(harness);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task 登録し直しの途中は専用の段階でだけ通る()
    {
        var harness = await SignInAsync(
            kind: QuestionnaireSessionKinds.Reenroll,
            scheme: AdminAuthSchemes.Reenroll);

        // **ログイン済み・途中状態としては使えない**
        var asSession = ContextOf(harness.HttpContext, harness.CookiePrincipal);
        await AdminSessionGuard.ValidateAsync(asSession);
        Assert.Null(asSession.Principal);

        var asPending = ContextOf(harness.HttpContext, harness.CookiePrincipal, AdminAuthSchemes.Pending);
        await AdminSessionGuard.ValidatePendingAsync(asPending);
        Assert.Null(asPending.Principal);

        var asReenroll = ContextOf(harness.HttpContext, harness.CookiePrincipal, AdminAuthSchemes.Reenroll);
        await AdminSessionGuard.ValidateReenrollAsync(asReenroll);
        Assert.NotNull(asReenroll.Principal);
    }

    // ---- SAML のログインで入ったセッション（Issue #191・#602） ----------------

    [Fact]
    public async Task SAMLで入ったセッションは単一ログアウトの手掛かりを載せる()
    {
        var harness = await SignInAsync(
            sso: new SessionSso(Saml: new SamlSessionKeys("name-id@example.test", "session-index-1")));

        var (context, _) = await ValidateAsync(harness);

        Assert.Equal("name-id@example.test", context.Principal!.FindFirstValue(AdminAuthSchemes.SamlNameIdClaim));
        Assert.Equal("session-index-1", context.Principal!.FindFirstValue(AdminAuthSchemes.SamlSessionIndexClaim));
    }

    // ---- Pleasanter のログインで入ったセッション（Issue #464） ----------------

    private static SessionSso PleasanterSso(int userId = 7, DateTimeOffset? verifiedAt = null) =>
        new(Pleasanter: new PleasanterSsoKeys(1, userId, verifiedAt ?? Start.AddHours(-1)));

    private static Action<IServiceCollection> Pleasanter(PleasanterSessionResult result) =>
        services => services
            .AddSingleton<IPleasanterSsoOptionsProvider>(new StaticPleasanterSsoOptionsProvider(
                new PleasanterSsoOptions
                {
                    Enabled = true,
                    InternalBaseUrl = new Uri("http://pleasanter/"),
                    LoginUrl = "/users/login",
                }))
            .AddSingleton<IPleasanterSessionVerifier>(new FakePleasanterSessionVerifier(result))
            .AddSingleton<PleasanterSsoSessionRevalidator>();

    [Fact]
    public async Task Pleasanterで同じ人のままならセッションを残す()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync(
            sso: PleasanterSso(),
            configure: Pleasanter(new PleasanterSessionResult(
                PleasanterSessionStatus.Authenticated,
                new PleasanterIdentity(1, 7, "admin", null)))));

        Assert.NotNull(context.Principal);
        Assert.Empty(harness.Auth.SignedOutSchemes);

        // **セッションの値から claim を組み立て直す**（再検証の入力になる）
        Assert.Equal("7", context.Principal!.FindFirstValue(PleasanterSsoClaims.UserId));
    }

    [Fact]
    public async Task Pleasanterでログアウトしていれば本アプリからも落とす()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync(
            sso: PleasanterSso(),
            configure: Pleasanter(PleasanterSessionResult.Unauthenticated("http-401"))));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
        Assert.Null(context.HttpContext.Items[PleasanterSsoSessionRevalidator.UpstreamErrorItemKey]);

        // **セッションも消す。** cookie だけ消してストアに残すと、一覧に生きたまま出る
        var store = context.HttpContext.RequestServices.GetRequiredService<IAdminSessionStore>();
        Assert.Empty(await store.ListForUserAsync(AdminUserId));
    }

    [Fact]
    public async Task Pleasanterに問い合わせられなければ落として503の印を付ける()
    {
        var (context, harness) = await ValidateAsync(await SignInAsync(
            sso: PleasanterSso(),
            configure: Pleasanter(PleasanterSessionResult.Error("timeout"))));

        Assert.Null(context.Principal);
        Assert.Contains(AdminAuthSchemes.Session, harness.Auth.SignedOutSchemes);
        Assert.Equal(true, context.HttpContext.Items[PleasanterSsoSessionRevalidator.UpstreamErrorItemKey]);
    }

    [Fact]
    public async Task 合言葉で入ったセッションはPleasanterに問い合わせない()
    {
        var verifier = new FakePleasanterSessionVerifier(PleasanterSessionResult.Error("unused"));
        var (context, _) = await ValidateAsync(await SignInAsync(
            configure: services => services
                .AddSingleton<IPleasanterSsoOptionsProvider>(
                    new StaticPleasanterSsoOptionsProvider(new PleasanterSsoOptions()))
                .AddSingleton<IPleasanterSessionVerifier>(verifier)
                .AddSingleton<PleasanterSsoSessionRevalidator>()));

        Assert.NotNull(context.Principal);
        Assert.Equal(0, verifier.Calls);
    }
}
