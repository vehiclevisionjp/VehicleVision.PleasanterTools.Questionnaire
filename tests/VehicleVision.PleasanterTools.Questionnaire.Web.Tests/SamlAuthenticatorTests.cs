using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>IdP から来た利用者の扱い（Issue #166）。</summary>
/// <remarks>
/// **拒絶と JIT 登録の 2 つを、既定が「拒絶」であることを含めて確かめる。**
/// </remarks>
public class SamlAuthenticatorTests
{
    private const string LoginId = "editor@example.jp";

    private sealed record Harness(
        FakeAdminUserStore Store,
        PasswordHasher Hasher,
        SamlAuthenticator Authenticator);

    private static Harness Create(
        SamlOptions options,
        TwoFactorPolicy twoFactor = TwoFactorPolicy.Optional)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var store = new FakeAdminUserStore(time);
        var hasher = new PasswordHasher();

        return new Harness(
            store,
            hasher,
            new SamlAuthenticator(
                store,
                hasher,
                options,
                new AdminAuthOptions { TwoFactor = twoFactor },
                NullLogger<SamlAuthenticator>.Instance));
    }

    private static SamlOptions Options(
        SamlUnknownUserPolicy unknownUser = SamlUnknownUserPolicy.Reject,
        AdminRole registerRole = AdminRole.Editor) =>
        new()
        {
            Enabled = true,
            EntityId = "https://questionnaire.example.jp",
            IdpEntityId = "https://idp.example.com/entity",
            SingleSignOnUrl = new Uri("https://idp.example.com/sso"),
            UnknownUser = unknownUser,
            RegisterRole = registerRole,
        };

    private static async Task<AdminUser> AddAsync(
        Harness harness,
        AdminRole role = AdminRole.Editor,
        bool isDisabled = false)
    {
        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = LoginId,
            PasswordHash = harness.Hasher.Hash("long-enough-password"),
            Role = role,
            IsDisabled = isDisabled,
        };

        await harness.Store.CreateAsync(user);
        return user;
    }

    [Fact]
    public async Task 既定では居ない利用者を通さない()
    {
        var harness = Create(Options());

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.Unknown, result.Outcome);
        Assert.Null(result.User);

        // **作っていない。** 拒絶は「入れない」だけでなく「増やさない」
        Assert.Empty(await harness.Store.ListAsync());
    }

    [Fact]
    public async Task 居る利用者はそのまま通る()
    {
        var harness = Create(Options());
        var user = await AddAsync(harness);

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal(user.AdminUserId, result.User!.AdminUserId);
        Assert.False(result.Registered);

        // **最終ログイン日時が入る。** 止め忘れを見つける手掛かり
        var stored = await harness.Store.FindByIdAsync(user.AdminUserId);
        Assert.NotNull(stored!.LastLoginAt);
    }

    [Fact]
    public async Task 前後の空白は落として突き合わせる()
    {
        var harness = Create(Options());
        await AddAsync(harness);

        var result = await harness.Authenticator.SignInAsync($"  {LoginId}\t");

        Assert.Equal(SamlSignInOutcome.SignedIn, result.Outcome);
    }

    [Fact]
    public async Task 登録する設定なら作って通す()
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register));

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.SignedIn, result.Outcome);
        Assert.True(result.Registered);
        Assert.Equal(AdminRole.Editor, result.User!.Role);

        var stored = Assert.Single(await harness.Store.ListAsync());
        Assert.Equal(LoginId, stored.LoginId);
    }

    [Fact]
    public async Task 登録で与える役割は設定で決まる()
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register, AdminRole.Administrator));

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(AdminRole.Administrator, result.User!.Role);
    }

    [Fact]
    public async Task 登録した利用者はパスワードでは通らない()
    {
        // **誰も知らない値を入れる。** パスワードのログインの道を残さない
        var harness = Create(Options(SamlUnknownUserPolicy.Register));
        await harness.Authenticator.SignInAsync(LoginId);

        var stored = Assert.Single(await harness.Store.ListAsync());

        Assert.NotEmpty(stored.PasswordHash);
        Assert.False(harness.Hasher.Verify(string.Empty, stored.PasswordHash).Verified);
        Assert.False(harness.Hasher.Verify(LoginId, stored.PasswordHash).Verified);
    }

    [Fact]
    public async Task 二度目は作り直さない()
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register));

        var first = await harness.Authenticator.SignInAsync(LoginId);
        var second = await harness.Authenticator.SignInAsync(LoginId);

        Assert.True(first.Registered);
        Assert.False(second.Registered);
        Assert.Equal(first.User!.AdminUserId, second.User!.AdminUserId);
        Assert.Single(await harness.Store.ListAsync());
    }

    [Fact]
    public async Task 止めた利用者は通さない()
    {
        var harness = Create(Options());
        await AddAsync(harness, isDisabled: true);

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.Disabled, result.Outcome);
    }

    [Fact]
    public async Task 必須なら二要素の登録を求める()
    {
        // ⚠️ **SAML を抜け道にできると、必須の設定が効かない**（Issue #154）
        var harness = Create(Options(), TwoFactorPolicy.Required);
        var user = await AddAsync(harness);

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.NeedsTotpEnrollment, result.Outcome);

        // **まだ通していないので、ログインは記録しない**
        var stored = await harness.Store.FindByIdAsync(user.AdminUserId);
        Assert.Null(stored!.LastLoginAt);
    }

    [Fact]
    public async Task 無効でも登録済みの二要素は省かない()
    {
        // **設定 1 つで既存の保護が消えるのは危ない**（Issue #154 の但し書き）
        var harness = Create(Options(), TwoFactorPolicy.Disabled);
        var user = await AddAsync(harness);
        await harness.Store.EnableTotpAsync(user.AdminUserId, "encrypted-secret");

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.NeedsSecondFactor, result.Outcome);
    }

    [Fact]
    public async Task 二要素を登録している人は二要素も通す()
    {
        // ⚠️ **IdP の多要素に任せきりにしない。**
        // 登録済みの相手が IdP 経由なら 1 要素で入れる、という穴を作らない
        var harness = Create(Options());
        var user = await AddAsync(harness);
        await harness.Store.EnableTotpAsync(user.AdminUserId, "encrypted-secret");

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.NeedsSecondFactor, result.Outcome);

        // **まだ通していないので、ログインは記録しない**
        var stored = await harness.Store.FindByIdAsync(user.AdminUserId);
        Assert.Null(stored!.LastLoginAt);
    }
}
