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
        FakeTimeProvider Time,
        SamlAuthenticator Authenticator);

    private sealed class StaticSamlOptionsProvider(SamlOptions options) : ISamlOptionsProvider
    {
        public Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SamlOptionsSnapshot(
                options,
                new SamlSettingValues(),
                new HashSet<string>()));

        public Task<SamlOptionsSnapshot> SaveAsync(
            SamlSettingValues values,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

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
            time,
            new SamlAuthenticator(
                store,
                hasher,
                new StaticSamlOptionsProvider(options),
                new AdminAuthOptions { TwoFactor = twoFactor },
                time,
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
        await Task.CompletedTask;
        return harness.Store.Seed(Guid.NewGuid(), LoginId, AdminRoleMap.ToName(role), isDisabled);
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
        Assert.Equal(user.Id, result.User!.Id);
        Assert.False(result.Registered);

        // **最終ログイン日時が入る。** 止め忘れを見つける手掛かり
        var stored = await harness.Store.FindByIdAsync(user.Id);
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
        Assert.Equal(nameof(AdminRole.Editor), result.User!.Role);

        var stored = Assert.Single(await harness.Store.ListAsync());
        Assert.Equal(LoginId, stored.LoginId);
    }

    [Fact]
    public async Task 登録で与える通常の役割は設定で決まる()
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register, AdminRole.SurveyAdministrator));

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(nameof(AdminRole.SurveyAdministrator), result.User!.Role);
    }

    [Theory]
    [InlineData(AdminRole.Administrator)]
    [InlineData((AdminRole)999)]
    public async Task 設定の解析を迂回しても禁止された役割では自動登録しない(AdminRole role)
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register, role));
        var result = await harness.Authenticator.SignInAsync(LoginId);
        Assert.Equal(SamlSignInOutcome.NotAllowed, result.Outcome);
        Assert.False(result.Registered);
        Assert.Empty(await harness.Store.ListAsync());
    }

    [Theory]
    [InlineData(TwoFactorPolicy.Optional, false)]
    [InlineData(TwoFactorPolicy.Optional, true)]
    [InlineData(TwoFactorPolicy.Required, false)]
    [InlineData(TwoFactorPolicy.Required, true)]
    [InlineData(TwoFactorPolicy.Disabled, false)]
    [InlineData(TwoFactorPolicy.Disabled, true)]
    public async Task 締め出し中は成功記録も二要素の開始もせず満了時に通す(TwoFactorPolicy policy, bool withTotp)
    {
        var harness = Create(Options(), policy);
        var user = await AddAsync(harness);
        if (withTotp)
        {
            await harness.Store.EnableTotpAsync(user.Id, "SECRET", 1, []);
        }
        await harness.Store.RecordFailureAsync(user.Id, 1, TimeSpan.FromMinutes(15));
        await harness.Store.RecordFailureAsync(user.Id, 5, TimeSpan.FromMinutes(15));
        var before = await harness.Store.FindByIdAsync(user.Id);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(SamlSignInOutcome.LockedOut, (await harness.Authenticator.SignInAsync(LoginId)).Outcome);
            Assert.Equal(before, await harness.Store.FindByIdAsync(user.Id));
        }
        harness.Time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));
        Assert.Equal(SamlSignInOutcome.LockedOut, (await harness.Authenticator.SignInAsync(LoginId)).Outcome);
        harness.Time.Advance(TimeSpan.FromTicks(1));
        var expected = withTotp ? SamlSignInOutcome.NeedsSecondFactor
            : policy == TwoFactorPolicy.Required ? SamlSignInOutcome.NeedsTotpEnrollment : SamlSignInOutcome.SignedIn;
        Assert.Equal(expected, (await harness.Authenticator.SignInAsync(LoginId)).Outcome);
    }

    [Fact]
    public async Task 登録した利用者はパスワードでは通らない()
    {
        // **誰も知らない値を入れる。** パスワードのログインの道を残さない
        var harness = Create(Options(SamlUnknownUserPolicy.Register));
        await harness.Authenticator.SignInAsync(LoginId);

        var (_, passwordHash) = (await harness.Store.FindForLoginAsync(LoginId))!.Value;

        Assert.NotEmpty(passwordHash);
        Assert.False(harness.Hasher.Verify(string.Empty, passwordHash).Verified);
        Assert.False(harness.Hasher.Verify(LoginId, passwordHash).Verified);
    }

    [Fact]
    public async Task 二度目は作り直さない()
    {
        var harness = Create(Options(SamlUnknownUserPolicy.Register));

        var first = await harness.Authenticator.SignInAsync(LoginId);
        var second = await harness.Authenticator.SignInAsync(LoginId);

        Assert.True(first.Registered);
        Assert.False(second.Registered);
        Assert.Equal(first.User!.Id, second.User!.Id);
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
        var stored = await harness.Store.FindByIdAsync(user.Id);
        Assert.Null(stored!.LastLoginAt);
    }

    [Fact]
    public async Task 無効でも登録済みの二要素は省かない()
    {
        // **設定 1 つで既存の保護が消えるのは危ない**（Issue #154 の但し書き）
        var harness = Create(Options(), TwoFactorPolicy.Disabled);
        var user = await AddAsync(harness);
        await harness.Store.EnableTotpAsync(user.Id, "SECRET", timeStep: 1, []);

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
        await harness.Store.EnableTotpAsync(user.Id, "SECRET", timeStep: 1, []);

        var result = await harness.Authenticator.SignInAsync(LoginId);

        Assert.Equal(SamlSignInOutcome.NeedsSecondFactor, result.Outcome);

        // **まだ通していないので、ログインは記録しない**
        var stored = await harness.Store.FindByIdAsync(user.Id);
        Assert.Null(stored!.LastLoginAt);
    }
}
