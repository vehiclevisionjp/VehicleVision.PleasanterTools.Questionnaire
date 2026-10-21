using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者の認証を 4 RDBMS で確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// 締め出しも使い捨てパスワードの使い回し防止も、
/// **どちらも DB の更新件数で判断している**。方言差で崩れないことをここで見る。
/// </para>
/// </remarks>
public class AdminAuthenticationTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private sealed record Harness(
        IAdminUserStore Store,
        IAdminUserSettingsStore Settings,
        DbConnectionFactory Factory,
        AdminAuthenticator Authenticator,
        PasswordHasher Hasher,
        TotpService Totp,
        SecretProtector Protector,
        FakeTimeProvider Time);

    private static Harness Create(
        DatabaseProvider provider,
        string connectionString,
        TwoFactorPolicy twoFactor = TwoFactorPolicy.Optional)
    {
        QuestionnaireDatabase.Instance.Migrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **検証用の DB なので、毎回まっさらにしてから始める**（初期設定の印も消す）
        AdminTestSupport.ClearAdministratorsAsync(factory).GetAwaiter().GetResult();

        var hasher = new PasswordHasher();
        var totp = new TotpService();
        var protector = new SecretProtector(SecretProtector.GenerateKey());
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T00:00:00Z"));
        var store = AdminTestSupport.CreateUserStore(factory, time, protector);
        var settings = new AdminUserSettingsStore(factory);

        var authenticator = new AdminAuthenticator(
            store,
            settings,
            hasher,
            totp,
            new AdminAuthOptions { TwoFactor = twoFactor },
            time,
            NullLogger<AdminAuthenticator>.Instance);

        return new Harness(store, settings, factory, authenticator, hasher, totp, protector, time);
    }

    private static string Code(string secret) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

    /// <summary>登録の手順を通さずに 2 要素を有効にする。</summary>
    /// <remarks>
    /// **登録で使った時間枠はそこで使い切る**ので、
    /// 続けて同じ数字でログインを試すと（正しく）弾かれる。
    /// ログイン側だけを見たいときは、この道で有効にする。
    /// </remarks>
    private static async Task EnrollDirectlyAsync(Harness harness, Guid adminUserId, string secret) =>
        await harness.Store.EnableTotpAsync(adminUserId, secret, timeStep: 0, []);

    /// <summary>この管理者の共有鍵の暗号文（DB の列そのまま）。</summary>
    private static async Task<string?> ReadCipherAsync(Harness harness, Guid adminUserId)
    {
        await using var connection = harness.Factory.Create();
        await connection.OpenAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<string?>(
            connection,
            SqlDialect.Format(
                harness.Factory.Provider, "SELECT [TotpSecretCipher] FROM [Core_AdminUsers] WHERE [Id] = @Id"),
            new { Id = adminUserId });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 回答通知メールは本人が有効にするまで送らない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator
            .TryCreateFirstAdministratorAsync("admin@example.test", "long-enough-password");

        Assert.NotNull(user);
        Assert.False(await harness.Settings.GetResponseNotificationEnabledAsync(user.Id));
        Assert.Empty(await harness.Settings.ListResponseNotificationRecipientsAsync());

        await harness.Settings.SetResponseNotificationEnabledAsync(user.Id, true);

        Assert.True(await harness.Settings.GetResponseNotificationEnabledAsync(user.Id));
        var recipient = Assert.Single(
            await harness.Settings.ListResponseNotificationRecipientsAsync());
        Assert.Equal(user.Id, recipient.AdminUserId);
        Assert.Equal("admin@example.test", recipient.LoginId);

        // 設定し直しても行は 1 つ（無効へ戻せる）。止めた管理者へは送らない
        await harness.Settings.SetResponseNotificationEnabledAsync(user.Id, false);
        Assert.Empty(await harness.Settings.ListResponseNotificationRecipientsAsync());
        await harness.Settings.SetResponseNotificationEnabledAsync(user.Id, true);
        await harness.Store.SetDisabledAsync(user.Id, true);
        Assert.Empty(await harness.Settings.ListResponseNotificationRecipientsAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 最初の管理者は一度だけ作れる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);

        var first = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        Assert.NotNull(first);
        Assert.Equal(nameof(AdminRole.Administrator), first.Role);

        // **既に居るなら通さない。** ここが二度使えると誰でも管理者になれる
        var second = await harness.Authenticator.TryCreateFirstAdministratorAsync("attacker", "long-enough-password");
        Assert.Null(second);

        // **初期設定の印が立つ。** 印は管理者が居なくなっても消えない（Core の窓口が主キーで排他する）
        await using var connection = harness.Factory.Create();
        await connection.OpenAsync();
        Assert.Equal(
            1,
            await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection, SqlDialect.Format(provider, "SELECT COUNT(*) FROM [Core_AdminSetup]")));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 必須ならパスワードが通っても二要素の登録を求める(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString, TwoFactorPolicy.Required);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        var result = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");

        // **必須にしている間は、パスワードだけでは通さない**
        Assert.Equal(PasswordOutcome.NeedsTotpEnrollment, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 任意なら二要素を登録していなくても通る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        // **既定は任意**（Issue #154）。登録していない利用者はそのまま通る
        var harness = Create(provider, connectionString);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        var result = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");

        Assert.Equal(PasswordOutcome.SignedIn, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 二要素を登録すると復旧コードが配られる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var enrollment = await harness.Authenticator.BeginTotpEnrollmentAsync(user!.Id, "admin");

        // **登録の途中の鍵も、暗号化して置く。** まだ有効にはならない
        Assert.False((await harness.Store.FindByIdAsync(user.Id))!.TotpEnabled);
        Assert.Equal(enrollment.SecretBase32, await harness.Authenticator.GetPendingEnrollmentSecretAsync(user.Id));

        var codes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, enrollment.SecretBase32, Code(enrollment.SecretBase32));

        Assert.NotNull(codes);
        Assert.Equal(RecoveryCode.Count, codes.Count);

        var stored = await harness.Store.FindByIdAsync(user.Id);
        Assert.NotNull(stored);
        Assert.True(stored.TotpEnabled);

        // 確定したら、途中の鍵は残らない
        Assert.Null(await harness.Authenticator.GetPendingEnrollmentSecretAsync(user.Id));

        // **平文で置かない。** 置いてあるのは、この管理者の文脈に束縛した暗号文
        var cipher = await ReadCipherAsync(harness, user.Id);
        Assert.NotEqual(enrollment.SecretBase32, cipher);
        Assert.Equal(
            enrollment.SecretBase32,
            harness.Protector.Unprotect(cipher!, $"{QuestionnaireDatabase.Instance.AppId}:totp:{user.Id:D}"));

        // 別の管理者の文脈では復号できない（暗号文を貼り替えても、その人の共有鍵としては読めない）
        Assert.Null(harness.Protector.Unprotect(cipher!, $"{QuestionnaireDatabase.Instance.AppId}:totp:{Guid.NewGuid():D}"));
        Assert.Equal(enrollment.SecretBase32, await harness.Store.GetTotpSecretAsync(user.Id));

        // 次のログインでは 2 要素を求める
        var result = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");
        Assert.Equal(PasswordOutcome.NeedsSecondFactor, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 同じ数字は二度通らない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var secret = harness.Totp.GenerateSecret();
        await EnrollDirectlyAsync(harness, user!.Id, secret);

        var code = Code(secret);
        Assert.Equal(
            SecondFactorOutcome.Succeeded,
            await harness.Authenticator.VerifyTotpAsync(user.Id, code));

        // **盗み見られた数字の二度目を弾く。** 30 秒の間は同じ数字が有効なため
        Assert.Equal(
            SecondFactorOutcome.Invalid,
            await harness.Authenticator.VerifyTotpAsync(user.Id, code));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 通ればログインの時刻が残る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var secret = harness.Totp.GenerateSecret();
        await EnrollDirectlyAsync(harness, user!.Id, secret);

        await harness.Authenticator.VerifyTotpAsync(user.Id, Code(secret));

        var stored = await harness.Store.FindByIdAsync(user.Id);
        // **止め忘れた管理者を見つける唯一の手掛かり**
        Assert.NotNull(stored!.LastLoginAt);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 復旧コードは一度しか使えない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var enrollment = await harness.Authenticator.BeginTotpEnrollmentAsync(user!.Id, "admin");
        var codes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, enrollment.SecretBase32, Code(enrollment.SecretBase32));

        var code = codes![0];

        Assert.Equal(
            SecondFactorOutcome.Succeeded,
            await harness.Authenticator.VerifyRecoveryCodeAsync(user.Id, code));

        Assert.Equal(
            SecondFactorOutcome.Invalid,
            await harness.Authenticator.VerifyRecoveryCodeAsync(user.Id, code));

        // 残りは使える
        Assert.Equal(
            SecondFactorOutcome.Succeeded,
            await harness.Authenticator.VerifyRecoveryCodeAsync(user.Id, codes[1]));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 復旧コードは書き方が違っても通る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var enrollment = await harness.Authenticator.BeginTotpEnrollmentAsync(user!.Id, "admin");
        var codes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, enrollment.SecretBase32, Code(enrollment.SecretBase32));

        // 区切りを外し、小文字で打ち直した形
        var typed = codes![0].Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();

        Assert.Equal(
            SecondFactorOutcome.Succeeded,
            await harness.Authenticator.VerifyRecoveryCodeAsync(user.Id, typed));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 復旧コードは保存された形から読み取れない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");
        var enrollment = await harness.Authenticator.BeginTotpEnrollmentAsync(user!.Id, "admin");
        var codes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, enrollment.SecretBase32, Code(enrollment.SecretBase32));

        var hashes = await harness.Settings.ListUnusedRecoveryCodeHashesAsync(user.Id);
        Assert.Equal(RecoveryCode.Count, hashes.Count);

        foreach (var hash in hashes)
        {
            foreach (var code in codes!)
            {
                // **ハッシュのみを持つ**
                Assert.DoesNotContain(RecoveryCode.Normalize(code), hash, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 失敗が続くと締め出す(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        var options = new AdminAuthOptions();
        for (var attempt = 0; attempt < options.MaxFailedAttempts; attempt++)
        {
            var failed = await harness.Authenticator.CheckPasswordAsync("admin", "wrong-password");
            Assert.Equal(PasswordOutcome.Invalid, failed.Outcome);
        }

        // **正しいパスワードでも通らない**
        var locked = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");
        Assert.Equal(PasswordOutcome.LockedOut, locked.Outcome);

        // **恒久的には締め出さない。** 時間が経てば通る
        harness.Time.Advance(options.LockoutDuration + TimeSpan.FromMinutes(1));
        var afterWait = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");
        Assert.Equal(PasswordOutcome.SignedIn, afterWait.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 止めた利用者は通らない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        await harness.Store.SetDisabledAsync(user!.Id, true);

        var result = await harness.Authenticator.CheckPasswordAsync("admin", "long-enough-password");
        Assert.Equal(PasswordOutcome.Disabled, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 居ない利用者はパスワードが違うのと同じ扱いになる(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        var missing = await harness.Authenticator.CheckPasswordAsync("nobody", "long-enough-password");
        var wrong = await harness.Authenticator.CheckPasswordAsync("admin", "wrong-password");

        // **区別できると、利用者名の総当たりに使える**
        Assert.Equal(PasswordOutcome.Invalid, missing.Outcome);
        Assert.Equal(PasswordOutcome.Invalid, wrong.Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 二要素を登録し直すと前の復旧コードは使えない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var user = await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", "long-enough-password");

        var first = await harness.Authenticator.BeginTotpEnrollmentAsync(user!.Id, "admin");
        var oldCodes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, first.SecretBase32, Code(first.SecretBase32));

        // 端末を替えて登録し直す
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await harness.Authenticator.BeginTotpEnrollmentAsync(user.Id, "admin");
        var newCodes = await harness.Authenticator.CompleteTotpEnrollmentAsync(
            user.Id, second.SecretBase32, Code(second.SecretBase32));

        Assert.NotNull(newCodes);

        // **古いコードは残さない。** 残すと、配り直した意味が無くなる
        Assert.Equal(
            SecondFactorOutcome.Invalid,
            await harness.Authenticator.VerifyRecoveryCodeAsync(user.Id, oldCodes![0]));
    }
}
