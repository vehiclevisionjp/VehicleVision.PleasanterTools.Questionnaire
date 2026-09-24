using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>Pleasanter から来た利用者の扱い（Issue #464）。**SAML と同じ判断であること**を確かめる。</summary>
public class PleasanterSsoAuthenticatorTests
{
    private const string LoginId = "Administrator";

    private sealed record Harness(
        FakeAdminUserStore Store,
        PasswordHasher Hasher,
        PleasanterSsoAuthenticator Authenticator);

    private static Harness Create(TwoFactorPolicy twoFactor = TwoFactorPolicy.Optional)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-24T00:00:00Z"));
        var store = new FakeAdminUserStore(time);
        var hasher = new PasswordHasher();
        return new Harness(
            store,
            hasher,
            new PleasanterSsoAuthenticator(
                store,
                hasher,
                new AdminAuthOptions { TwoFactor = twoFactor },
                NullLogger<PleasanterSsoAuthenticator>.Instance));
    }

    private static PleasanterSsoOptions Options(
        PleasanterSsoUnknownUserPolicy unknownUser = PleasanterSsoUnknownUserPolicy.Reject,
        AdminRole registerRole = AdminRole.Editor) => new()
        {
            Enabled = true,
            InternalBaseUrl = new Uri("http://pleasanter/"),
            LoginUrl = "/users/login",
            UnknownUser = unknownUser,
            RegisterRole = registerRole,
        };

    private static async Task<AdminUser> AddAsync(Harness harness, bool isDisabled = false)
    {
        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = LoginId,
            PasswordHash = harness.Hasher.Hash("long-enough-password"),
            Role = AdminRole.Editor,
            IsDisabled = isDisabled,
        };
        await harness.Store.CreateAsync(user);
        return user;
    }

    [Fact]
    public async Task 既定では居ない利用者を通さず作らない()
    {
        var harness = Create();

        var result = await harness.Authenticator.SignInAsync(LoginId, Options());

        Assert.Equal(PleasanterSsoSignInOutcome.Unknown, result.Outcome);
        Assert.Empty(await harness.Store.ListAsync());
    }

    [Fact]
    public async Task 居る利用者はそのまま通りログインを記録する()
    {
        var harness = Create();
        var user = await AddAsync(harness);

        var result = await harness.Authenticator.SignInAsync($" {LoginId} ", Options());

        Assert.Equal(PleasanterSsoSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal(user.AdminUserId, result.User!.AdminUserId);
        Assert.NotNull((await harness.Store.FindByIdAsync(user.AdminUserId))!.LastLoginAt);
    }

    [Fact]
    public async Task 登録する設定なら設定の役割で作りパスワードでは入れない()
    {
        var harness = Create();

        var result = await harness.Authenticator.SignInAsync(
            LoginId,
            Options(PleasanterSsoUnknownUserPolicy.Register, AdminRole.SurveyAdministrator));

        Assert.Equal(PleasanterSsoSignInOutcome.SignedIn, result.Outcome);
        Assert.True(result.Registered);
        var stored = Assert.Single(await harness.Store.ListAsync());
        Assert.Equal(AdminRole.SurveyAdministrator, stored.Role);
        Assert.False(harness.Hasher.Verify(string.Empty, stored.PasswordHash).Verified);
        Assert.False(harness.Hasher.Verify(LoginId, stored.PasswordHash).Verified);
    }

    [Fact]
    public async Task 止めた利用者は通さない()
    {
        var harness = Create();
        await AddAsync(harness, isDisabled: true);

        var result = await harness.Authenticator.SignInAsync(LoginId, Options());

        Assert.Equal(PleasanterSsoSignInOutcome.Disabled, result.Outcome);
    }

    [Fact]
    public async Task 本アプリで二要素を登録している人は二要素も通す()
    {
        // ⚠️ **Pleasanter 側の 2 要素に任せきりにしない**（SAML と同じ）
        var harness = Create(TwoFactorPolicy.Disabled);
        var user = await AddAsync(harness);
        await harness.Store.EnableTotpAsync(user.AdminUserId, "encrypted-secret");

        var result = await harness.Authenticator.SignInAsync(LoginId, Options());

        Assert.Equal(PleasanterSsoSignInOutcome.NeedsSecondFactor, result.Outcome);
        Assert.Null((await harness.Store.FindByIdAsync(user.AdminUserId))!.LastLoginAt);
    }

    [Fact]
    public async Task 必須なら二要素の登録を求める()
    {
        var harness = Create(TwoFactorPolicy.Required);
        await AddAsync(harness);

        var result = await harness.Authenticator.SignInAsync(LoginId, Options());

        Assert.Equal(PleasanterSsoSignInOutcome.NeedsTotpEnrollment, result.Outcome);
    }
}

/// <summary>Pleasanter のログインで入ったセッションの再検証（Issue #464）。</summary>
public class PleasanterSsoSessionRevalidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PleasanterSsoOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        InternalBaseUrl = new Uri("http://pleasanter/"),
        LoginUrl = "/users/login",
        RevalidateInterval = TimeSpan.FromMinutes(5),
    };

    private static System.Security.Claims.ClaimsPrincipal Principal(DateTimeOffset verifiedAt, int userId = 7) =>
        new(new System.Security.Claims.ClaimsIdentity(
            PleasanterSsoClaims.Create(new PleasanterIdentity(1, userId, "admin", null), verifiedAt),
            "test"));

    private static (PleasanterSsoSessionRevalidator Revalidator, FakePleasanterSessionVerifier Verifier, FakeTimeProvider Time)
        Create(PleasanterSessionResult result, bool enabled = true)
    {
        var time = new FakeTimeProvider(Now);
        var verifier = new FakePleasanterSessionVerifier(result);
        return (
            new PleasanterSsoSessionRevalidator(
                new StaticPleasanterSsoOptionsProvider(Options(enabled)),
                verifier,
                time,
                NullLogger<PleasanterSsoSessionRevalidator>.Instance),
            verifier,
            time);
    }

    private static PleasanterSessionResult Same() =>
        new(PleasanterSessionStatus.Authenticated, new PleasanterIdentity(1, 7, "admin", null));

    private static DefaultHttpContext Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".AspNetCore.Cookies=abc; q.admin=own";
        return context;
    }

    [Fact]
    public async Task 印の無いセッションは対象外()
    {
        var (revalidator, verifier, _) = Create(Same());

        var result = await revalidator.RevalidateAsync(
            Request(),
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()),
            SessionId);

        Assert.Equal(PleasanterSsoRevalidation.NotApplicable, result);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task 間隔内なら問い合わせずに通す()
    {
        var (revalidator, verifier, _) = Create(PleasanterSessionResult.Error("unused"));

        var result = await revalidator.RevalidateAsync(Request(), Principal(Now.AddMinutes(-4)), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Valid, result);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task 間隔を過ぎたら要求のcookieで問い合わせ同じ人なら次の間隔まで覚える()
    {
        var (revalidator, verifier, time) = Create(Same());

        var first = await revalidator.RevalidateAsync(Request(), Principal(Now.AddMinutes(-6)), SessionId);
        Assert.Equal(PleasanterSsoRevalidation.Valid, first);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(".AspNetCore.Cookies=abc; q.admin=own", verifier.LastCookies.ToString());

        // **覚えた時刻から数える。** セッションに載ったログイン時の時刻のままだと、毎回問い合わせる
        time.Advance(TimeSpan.FromMinutes(4));
        await revalidator.RevalidateAsync(Request(), Principal(Now.AddMinutes(-6)), SessionId);
        Assert.Equal(1, verifier.Calls);

        time.Advance(TimeSpan.FromMinutes(2));
        await revalidator.RevalidateAsync(Request(), Principal(Now.AddMinutes(-6)), SessionId);
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task 別人に替わっていれば落とす()
    {
        var (revalidator, _, _) = Create(
            new PleasanterSessionResult(PleasanterSessionStatus.Authenticated, new PleasanterIdentity(1, 8, "other", null)));

        var result = await revalidator.RevalidateAsync(Request(), Principal(Now.AddHours(-1)), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Rejected, result);
    }

    [Fact]
    public async Task 別テナントの同じ利用者IDでも落とす()
    {
        var (revalidator, _, _) = Create(
            new PleasanterSessionResult(PleasanterSessionStatus.Authenticated, new PleasanterIdentity(2, 7, "admin", null)));

        var result = await revalidator.RevalidateAsync(Request(), Principal(Now.AddHours(-1)), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Rejected, result);
    }

    [Fact]
    public async Task ログアウトしていれば落とす()
    {
        var (revalidator, _, _) = Create(PleasanterSessionResult.Unauthenticated("http-401"));

        var result = await revalidator.RevalidateAsync(Request(), Principal(Now.AddHours(-1)), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Rejected, result);
    }

    [Fact]
    public async Task 問い合わせられなければ延ばさずに落とす()
    {
        var (revalidator, verifier, time) = Create(Same());
        await revalidator.RevalidateAsync(Request(), Principal(Now.AddHours(-1)), SessionId);

        // **一度通った後でも、問い合わせられなければ通さない**
        verifier.Result = PleasanterSessionResult.Error("timeout");
        time.Advance(TimeSpan.FromMinutes(6));
        var result = await revalidator.RevalidateAsync(Request(), Principal(Now.AddHours(-1)), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.UpstreamError, result);
    }

    [Fact]
    public async Task 機能を止めたらそれで入った人も落とす()
    {
        var (revalidator, verifier, _) = Create(Same(), enabled: false);

        var result = await revalidator.RevalidateAsync(Request(), Principal(Now), SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Rejected, result);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task 確かめた時刻が読めなければ問い合わせる()
    {
        var (revalidator, verifier, _) = Create(Same());
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(PleasanterSsoClaims.TenantId, "1"),
                new System.Security.Claims.Claim(PleasanterSsoClaims.UserId, "7"),
                new System.Security.Claims.Claim(PleasanterSsoClaims.VerifiedAt, "not-a-number"),
            ],
            "test"));

        var result = await revalidator.RevalidateAsync(Request(), principal, SessionId);

        Assert.Equal(PleasanterSsoRevalidation.Valid, result);
        Assert.Equal(1, verifier.Calls);
    }
}
