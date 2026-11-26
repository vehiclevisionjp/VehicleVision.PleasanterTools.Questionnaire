using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>パスキーの受け口のアダプターの、本アプリ固有の判断を確かめる（Issue #1082）。</summary>
public class AdminPasskeyAdaptersTests
{
    private const string Password = "long-enough-password";

    private sealed class StaticSamlProvider(bool enabled) : ISamlOptionsProvider
    {
        public Task<SamlOptionsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SamlOptionsSnapshot(
                new SamlOptions { Enabled = enabled },
                new SamlSettingValues(),
                new HashSet<string>()));

        public Task<SamlOptionsSnapshot> SaveAsync(
            SamlSettingValues values,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record Harness(
        FakeAdminUserStore Users,
        FakeAdminSessionStore Sessions,
        FakeAdminInvitationStore Invitations,
        FakeTimeProvider Time,
        PasskeyAccountsAdapter Accounts,
        AdminUser User);

    private static async Task<Harness> CreateAsync(
        bool passwordSignInEnabled = true, bool saml = false, bool pleasanterSso = false)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-25T00:00:00Z"));
        var users = new FakeAdminUserStore(time);
        var sessions = new FakeAdminSessionStore(time);
        var invitations = new FakeAdminInvitationStore(time);
        var hasher = new PasswordHasher();
        var authenticator = new AdminAuthenticator(
            users, users, hasher, new TotpService(), new AdminAuthOptions(), time, NullLogger<AdminAuthenticator>.Instance);
        var user = await users.CreateAsync("passkey@example.test", hasher.Hash(Password), "Editor", "ja")
            ?? throw new InvalidOperationException("利用者を作れませんでした。");

        var accounts = new PasskeyAccountsAdapter(
            users,
            users,
            sessions,
            authenticator,
            invitations,
            new FakeAdminSignInSettings(passwordSignInEnabled),
            new StaticSamlProvider(saml),
            new StaticPleasanterSsoOptionsProvider(new PleasanterSsoOptions { Enabled = pleasanterSso }),
            time);
        return new Harness(users, sessions, invitations, time, accounts, user);
    }

    // ---- 使えるログイン方法（Core の P5） ----

    [Fact]
    public async Task 既定ではパスワードで入れる()
    {
        var harness = await CreateAsync();

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.Equal(new PasskeySignInMethods(Password: true, Saml: false, PleasanterSso: false), methods);
        Assert.True(methods.Any);
    }

    [Fact]
    public async Task パスワード未設定の管理者はパスワードを使えるログイン方法に数えない()
    {
        var harness = await CreateAsync();
        await harness.Users.SetPasswordSetAsync(harness.User.Id, false);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.Equal(new PasskeySignInMethods(Password: false, Saml: false, PleasanterSso: false), methods);
        Assert.False(methods.Any);
    }

    [Fact]
    public async Task パスワード未設定の管理者でも_SAMLが有効ならSAMLを数える()
    {
        var harness = await CreateAsync(passwordSignInEnabled: false, saml: true);
        await harness.Users.SetPasswordSetAsync(harness.User.Id, false);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.Equal(new PasskeySignInMethods(Password: false, Saml: true, PleasanterSso: false), methods);
        Assert.True(methods.Any);
    }

    [Fact]
    public async Task パスワード未設定の管理者でも_Pleasanterのログインが有効ならそれを数える()
    {
        var harness = await CreateAsync(pleasanterSso: true);
        await harness.Users.SetPasswordSetAsync(harness.User.Id, false);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        // 合言葉は塞がれていない（既定）が、印が未設定なので数えない。Pleasanter は数える
        Assert.Equal(new PasskeySignInMethods(Password: false, Saml: false, PleasanterSso: true), methods);
    }

    [Fact]
    public async Task パスワード未設定の印はログインの成否を変えない()
    {
        // 印は「使えるログイン方法」の数え方だけを決める。パスワードの照合は、ハッシュだけが決める
        var harness = await CreateAsync();
        await harness.Users.SetPasswordSetAsync(harness.User.Id, false);

        Assert.Equal(PasswordConfirmation.Confirmed, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, Password));
    }

    [Fact]
    public async Task 招待を受け取っていない人はパスワードで入れない()
    {
        var harness = await CreateAsync();
        await harness.Invitations.ReplaceAsync(new AdminInvitation(
            Guid.NewGuid(), harness.User.Id, "hash", harness.Time.GetUtcNow().AddHours(1)));

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.False(methods.Password);
        Assert.False(methods.Any);
    }

    [Fact]
    public async Task 合言葉のログインを塞いでいても_SAMLが有効ならSAMLで入れる()
    {
        var harness = await CreateAsync(passwordSignInEnabled: false, saml: true);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.Equal(new PasskeySignInMethods(Password: false, Saml: true, PleasanterSso: false), methods);
    }

    [Fact]
    public async Task 合言葉のログインを塞いでいてPleasanterのログインが有効ならそちらで入れる()
    {
        var harness = await CreateAsync(passwordSignInEnabled: false, pleasanterSso: true);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.Equal(new PasskeySignInMethods(Password: false, Saml: false, PleasanterSso: true), methods);
    }

    [Fact]
    public async Task 合言葉を塞ぐ指定は_ほかの入口が無い間は効かずパスワードで入れる()
    {
        // 塞げるのは SAML か Pleasanter のログインが有効な間だけ（AdminPasswordSignInPolicy）
        var harness = await CreateAsync(passwordSignInEnabled: false);

        var methods = await harness.Accounts.GetSignInMethodsAsync(harness.User.Id);

        Assert.True(methods.Password);
    }

    // ---- パスワードの再確認（ログインと同じ道） ----

    [Fact]
    public async Task 再確認は合っていれば通り_違えば失敗を数えて締め出す()
    {
        var harness = await CreateAsync();
        var maxFailed = new AdminAuthOptions().MaxFailedAttempts;

        Assert.Equal(PasswordConfirmation.Confirmed, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, Password));
        for (var i = 0; i < maxFailed; i++)
        {
            Assert.Equal(PasswordConfirmation.Rejected, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, "wrong"));
        }

        // 規定回数の失敗で締め出され、合っているパスワードでも通らない
        Assert.Equal(PasswordConfirmation.Locked, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, Password));
        Assert.True((await harness.Accounts.FindAsync(harness.User.Id))!.IsLocked);

        // 締め出しが明ければ通り、失敗の回数は戻る
        harness.Time.Advance(new AdminAuthOptions().LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(PasswordConfirmation.Confirmed, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, Password));
        Assert.Equal(0, (await harness.Users.FindByIdAsync(harness.User.Id))!.FailedLoginCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-password")]
    public async Task 空や違うパスワードは通さない(string password)
    {
        var harness = await CreateAsync();

        Assert.Equal(PasswordConfirmation.Rejected, await harness.Accounts.ConfirmPasswordAsync(harness.User.Id, password));
    }

    [Fact]
    public async Task 居ない管理者の再確認は失敗として扱う()
    {
        var harness = await CreateAsync();

        Assert.Equal(PasswordConfirmation.Rejected, await harness.Accounts.ConfirmPasswordAsync(Guid.NewGuid(), Password));
        Assert.Null(await harness.Accounts.FindAsync(Guid.NewGuid()));
    }

    // ---- 全セッションの失効 ----

    [Fact]
    public async Task 全セッションの失効は段階を問わず持ち主の分だけを消す()
    {
        var harness = await CreateAsync();
        var other = Guid.NewGuid();
        var session = await harness.Sessions.CreateAsync(harness.User.Id, AdminSessionKinds.Session, TimeSpan.FromHours(1));
        var pending = await harness.Sessions.CreateAsync(harness.User.Id, AdminSessionKinds.Pending, TimeSpan.FromMinutes(5));
        var othersSession = await harness.Sessions.CreateAsync(other, AdminSessionKinds.Session, TimeSpan.FromHours(1));

        await harness.Accounts.RevokeSessionsAsync(harness.User.Id);

        Assert.Null(await harness.Sessions.FindAsync(session));
        Assert.Null(await harness.Sessions.FindAsync(pending));
        Assert.NotNull(await harness.Sessions.FindAsync(othersSession));
    }

    // ---- 監査 ----

    private sealed class RecordingAuditLogStore(bool throws = false) : IAuditLogStore
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (throws)
            {
                throw new InvalidOperationException("書けない");
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOlderThanAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static PasskeyAuditEntry Entry(DateTimeOffset at) =>
        new(at, PasskeyAuditActions.SignInFailed, Succeeded: false, StatusCode: 401, ActorKey: null, ActorLoginId: null,
            TargetId: null, DetailJson: "{\"reason\":\"unknown-credential\"}", IpAddress: "192.0.2.1");

    [Fact]
    public async Task 監査は対象をAdminUserにし_時刻はこれまでの目盛り_ローカル時刻の数字_で残す()
    {
        var store = new RecordingAuditLogStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-25T00:00:00Z"));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"));
        var sink = new PasskeyAuditSink(store, time, NullLogger<PasskeyAuditSink>.Instance);

        await sink.WriteAsync(Entry(DateTimeOffset.Parse("2026-11-25T00:00:00Z")));

        var written = Assert.Single(store.Entries);
        Assert.Equal("AdminUser", written.TargetType);
        Assert.Equal(PasskeyAuditActions.SignInFailed, written.Action);
        Assert.False(written.Succeeded);
        Assert.Contains("unknown-credential", written.DetailJson, StringComparison.Ordinal);
        // UTC の 00:00 は、ローカル時刻（JST）の 09:00。その数字を、そのまま時刻の数字として残す
        Assert.Equal(new DateTime(2026, 11, 25, 9, 0, 0), AuditLogTime.ToWallClock(written.OccurredAt));
    }

    [Fact]
    public async Task 監査を書けなくても操作は通す()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-25T00:00:00Z"));
        var sink = new PasskeyAuditSink(new RecordingAuditLogStore(throws: true), time, NullLogger<PasskeyAuditSink>.Instance);

        await sink.WriteAsync(Entry(time.GetUtcNow()));
    }

    // ---- 権限 ----

    [Fact]
    public void 他人のパスキーを消す権限は_特権管理者とユーザ管理者だけが持ち_2要素の解除とは別に数える()
    {
        foreach (var role in Enum.GetValues<AdminRole>())
        {
            Assert.Equal(
                role is AdminRole.Administrator or AdminRole.UserAdministrator,
                AdminPermissions.Of(role).Contains(AdminPermissions.UsersResetPasskeys));
        }

        Assert.Equal("users.resetPasskeys", AdminPermissions.UsersResetPasskeys);
        Assert.NotEqual(AdminPermissions.UsersResetTwoFactor, AdminPermissions.UsersResetPasskeys);
        Assert.Contains(AdminPermissions.UsersResetPasskeys, AdminPermissions.All);
    }
}
