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
        var protector = new SecretProtector(SecretProtector.GenerateKey());

        var authenticator = new AdminAuthenticator(
            users,
            new PasswordHasher(),
            new TotpService(),
            protector,
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
        await harness.Users.EnableTotpAsync(user.AdminUserId, "protected-secret");
        await harness.Users.ReplaceRecoveryCodesAsync(user.AdminUserId, ["hash-1", "hash-2"]);
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
    public void 無効なら登録の口を開かない()
    {
        var harness = Create(TwoFactorPolicy.Disabled);

        // **画面から隠すだけでは足りない。** API を直接叩かれても通さない
        Assert.Throws<InvalidOperationException>(() => harness.Authenticator.BeginTotpEnrollment("admin"));
    }

    [Theory]
    [InlineData(TwoFactorPolicy.Required)]
    [InlineData(TwoFactorPolicy.Optional)]
    public void 必須と任意では登録の口が開く(TwoFactorPolicy policy)
    {
        var harness = Create(policy);

        var enrollment = harness.Authenticator.BeginTotpEnrollment("admin");

        Assert.NotEmpty(enrollment.SecretBase32);
        Assert.Contains("otpauth://", enrollment.OtpAuthUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解除すると共有鍵も復旧コードも消える()
    {
        var harness = Create(TwoFactorPolicy.Optional);
        var user = await EnrolledAdministratorAsync(harness);

        await harness.Authenticator.DisableTotpAsync(user.AdminUserId);

        var after = await harness.Users.FindByIdAsync(user.AdminUserId);
        Assert.NotNull(after);
        Assert.False(after!.HasTotp);
        Assert.Null(after.TotpSecretEncrypted);

        // **復旧コードだけ残っても意味が無い**
        Assert.Empty(await harness.Users.ListUnusedRecoveryCodesAsync(user.AdminUserId));
    }

    /// <summary>必須の設定で解除された人は、**次回ログイン時に登録へ回る。**</summary>
    [Fact]
    public async Task 必須で解除された相手は次のログインで登録へ回る()
    {
        var harness = Create(TwoFactorPolicy.Required);
        var user = await EnrolledAdministratorAsync(harness);

        await harness.Authenticator.DisableTotpAsync(user.AdminUserId);
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

        var after = await harness.Users.FindByIdAsync(user.AdminUserId);
        Assert.Null(after!.LastLoginAt);

        // **記録するのはセッションを張る側**
        await harness.Authenticator.RecordSignInAsync(user.AdminUserId);
        Assert.NotNull((await harness.Users.FindByIdAsync(user.AdminUserId))!.LastLoginAt);
    }
}
