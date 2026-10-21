using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>2 要素認証の必須・任意・無効の切り替え（Issue #154）。</summary>
/// <remarks>
/// **設定 1 つで既存の保護が消えないこと**が、ここでいちばん確かめたいこと。
/// </remarks>
public class AdminTwoFactorPolicyTests
{
    private const string Password = "long-enough-password";
    private sealed record Harness(
        FakeAdminUserStore Users,
        AdminAuthenticator Authenticator,
        FakeTimeProvider Time);

    private static Harness Create(TwoFactorPolicy policy)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-08T00:00:00Z"));
        var users = new FakeAdminUserStore(time);

        var authenticator = new AdminAuthenticator(
            users,
            users,
            new PasswordHasher(),
            new TotpService(),
            new AdminAuthOptions { TwoFactor = policy },
            time,
            NullLogger<AdminAuthenticator>.Instance);

        return new Harness(users, authenticator, time);
    }

    /// <summary>2 要素を登録済みの管理者を作る。</summary>
    private static async Task<AdminUser> EnrolledAdministratorAsync(Harness harness)
    {
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password))!;

        // **共有鍵の中身はここでは問わない。** 「登録されている」状態を作るだけ
        await harness.Users.EnableTotpAsync(user.Id, "SECRET", timeStep: 1, ["hash-1", "hash-2"]);
        return user;
    }

    [Fact]
    public async Task 必須なら未登録の相手には登録を求める()
    {
        var harness = Create(TwoFactorPolicy.Required);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password);

        var result = await harness.Authenticator.CheckPasswordAsync("admin", Password);

        Assert.Equal(PasswordOutcome.NeedsTotpEnrollment, result.Outcome);
    }

    [Fact]
    public async Task 任意なら未登録の相手はそのまま入れる()
    {
        var harness = Create(TwoFactorPolicy.Optional);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password);

        var result = await harness.Authenticator.CheckPasswordAsync("admin", Password);

        Assert.Equal(PasswordOutcome.SignedIn, result.Outcome);
    }

    [Fact]
    public async Task 無効でも未登録の相手はそのまま入れる()
    {
        var harness = Create(TwoFactorPolicy.Disabled);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password);

        var result = await harness.Authenticator.CheckPasswordAsync("admin", Password);

        Assert.Equal(PasswordOutcome.SignedIn, result.Outcome);
    }

    /// <summary>**ここが肝。** 設定を無効にしても、登録済みの人の保護は外れない。</summary>
    [Theory]
    [InlineData(TwoFactorPolicy.Required)]
    [InlineData(TwoFactorPolicy.Optional)]
    [InlineData(TwoFactorPolicy.Disabled)]
    public async Task 登録済みの相手にはどの設定でも二要素を求める(TwoFactorPolicy policy)
    {
        var harness = Create(policy);
        await EnrolledAdministratorAsync(harness);

        var result = await harness.Authenticator.CheckPasswordAsync("admin", Password);

        Assert.Equal(PasswordOutcome.NeedsSecondFactor, result.Outcome);
    }

    [Fact]
    public async Task 無効なら登録の口を開かない()
    {
        var harness = Create(TwoFactorPolicy.Disabled);
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password))!;

        // **画面から隠すだけでは足りない。** API を直接叩かれても通さない
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Authenticator.BeginTotpEnrollmentAsync(user.Id, "admin"));
        Assert.Null(await harness.Users.GetTotpPendingAsync(user.Id));
    }

    [Theory]
    [InlineData(TwoFactorPolicy.Required)]
    [InlineData(TwoFactorPolicy.Optional)]
    public async Task 必須と任意では登録の口が開く(TwoFactorPolicy policy)
    {
        var harness = Create(policy);
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password))!;

        var enrollment = await harness.Authenticator.BeginTotpEnrollmentAsync(user.Id, "admin");

        Assert.NotEmpty(enrollment.SecretBase32);
        Assert.Contains("otpauth://", enrollment.OtpAuthUri, StringComparison.Ordinal);

        // **共有鍵はサーバ側に「登録の途中の鍵」として預かる。** まだ有効にはならない
        Assert.Equal(enrollment.SecretBase32, await harness.Authenticator.GetPendingEnrollmentSecretAsync(user.Id));
        Assert.False((await harness.Users.FindByIdAsync(user.Id))!.TotpEnabled);
    }

    [Fact]
    public async Task 解除すると共有鍵も復旧コードも消える()
    {
        var harness = Create(TwoFactorPolicy.Optional);
        var user = await EnrolledAdministratorAsync(harness);

        await harness.Authenticator.DisableTotpAsync(user.Id);

        var after = await harness.Users.FindByIdAsync(user.Id);
        Assert.NotNull(after);
        Assert.False(after!.TotpEnabled);
        Assert.Null(harness.Users.TotpSecretOf(user.Id));

        // **復旧コードだけ残っても意味が無い**
        Assert.Equal(0, await harness.Users.CountRecoveryCodesAsync(user.Id));
    }

    /// <summary>必須の設定で解除された人は、**次回ログイン時に登録へ回る。**</summary>
    [Fact]
    public async Task 必須で解除された相手は次のログインで登録へ回る()
    {
        var harness = Create(TwoFactorPolicy.Required);
        var user = await EnrolledAdministratorAsync(harness);

        await harness.Authenticator.DisableTotpAsync(user.Id);
        var result = await harness.Authenticator.CheckPasswordAsync("admin", Password);

        Assert.Equal(PasswordOutcome.NeedsTotpEnrollment, result.Outcome);
    }

    /// <summary>
    /// **パスワードの照合そのものでログインを記録しないこと。**
    /// </summary>
    /// <remarks>
    /// この照合は「パスワードの再確認」（2 要素の登録し直しなど）にも使われる。
    /// ここで記録すると、**入っていないのに入ったことになる。**
    /// </remarks>
    [Fact]
    public async Task 照合だけではログインを記録しない()
    {
        var harness = Create(TwoFactorPolicy.Optional);
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password))!;

        await harness.Authenticator.CheckPasswordAsync("admin", Password);

        var after = await harness.Users.FindByIdAsync(user.Id);
        Assert.Null(after!.LastLoginAt);

        // **記録するのはセッションを張る側**
        await harness.Authenticator.RecordSignInAsync(user.Id);
        Assert.NotNull((await harness.Users.FindByIdAsync(user.Id))!.LastLoginAt);
    }
}
