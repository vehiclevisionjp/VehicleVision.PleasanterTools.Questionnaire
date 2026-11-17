using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理 API の個人トークン（API キー）の発行・失効・検証（Issue #1025）。</summary>
/// <remarks>
/// DB を立てずに、決まり（ハッシュだけ保存・期限必須・スコープの制限・役割ごとの可否・失効・自動失効・平文を残さない）を見る。
/// 本物の SQL は結合試験（<c>AdminApiTokenStoreTests</c>）、HTTP は <c>AdminApiTokenEndToEndTests</c> の受け持ち。
/// </remarks>
public class AdminApiTokenServiceTests
{
    private const string Password = "long-enough-password";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? string.Empty : exception.ToString()));
    }

    private sealed record Harness(
        FakeAdminUserStore Users,
        FakeAdminApiTokenStore Tokens,
        FakeAdminSessionStore Sessions,
        AdminUserService UserService,
        AdminApiTokenService Service,
        FakeTimeProvider Time,
        CapturingLogger<AdminApiTokenService> Log);

    private static Harness Create(AdminApiTokenOptions? options = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-18T00:00:00Z"));
        var users = new FakeAdminUserStore(time);
        var sessions = new FakeAdminSessionStore(time);
        var invitations = new FakeAdminInvitationStore(time);
        var tokens = new FakeAdminApiTokenStore();
        var hasher = new PasswordHasher();
        var authOptions = new AdminAuthOptions();

        var authenticator = new AdminAuthenticator(
            users,
            users,
            hasher,
            new TotpService(),
            authOptions,
            time,
            NullLogger<AdminAuthenticator>.Instance);
        var userService = new AdminUserService(
            users,
            sessions,
            invitations,
            tokens,
            authenticator,
            hasher,
            authOptions,
            new AdminPasswordPolicy(new AdminPasswordPolicyOptions()),
            time,
            NullLogger<AdminUserService>.Instance);

        var log = new CapturingLogger<AdminApiTokenService>();
        var service = new AdminApiTokenService(
            tokens,
            users,
            userService,
            options ?? new AdminApiTokenOptions { Enabled = true },
            time,
            log);

        return new Harness(users, tokens, sessions, userService, service, time, log);
    }

    /// <summary>役割を指定して管理者を作る（パスワードは <see cref="Password"/>）。</summary>
    private static async Task<AdminUser> UserAsync(Harness harness, AdminRole role, string loginId)
    {
        var user = await harness.Users.CreateAsync(
            loginId, new PasswordHasher().Hash(Password), AdminRoleMap.ToName(role), string.Empty);
        return user!;
    }

    private static AdminApiTokenIssueRequest Request(
        string name = "ci",
        string[]? scopes = null,
        int? days = null,
        string[]? ips = null) =>
        new(name, scopes ?? [AdminPermissions.SurveysRead], days, ips);

    private static AdminApiTokenReauthentication.Password Reauth(string password = Password) => new(password);

    private static async Task<IssuedAdminApiToken> IssueAsync(
        Harness harness,
        AdminUser owner,
        AdminApiTokenIssueRequest? request = null)
    {
        var (outcome, issued) = await harness.Service.IssueAsync(owner.Id, request ?? Request(), Reauth());
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, outcome);
        return issued!;
    }

    // ---- 発行（ハッシュだけ保存・平文は 1 回だけ） ------------------------------------

    [Fact]
    public async Task 発行したトークンはハッシュだけを保存し_平文はどこにも残らない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var issued = await IssueAsync(harness, owner, Request(scopes: [AdminPermissions.SurveysRead, AdminPermissions.SurveysWrite]));

        Assert.StartsWith("qat_", issued.Token, StringComparison.Ordinal);
        Assert.Equal(AdminApiTokenSecret.HashOf(issued.Token), Assert.Single(harness.Tokens.StoredHashes));
        Assert.Equal(issued.Token[..AdminApiTokenSecret.PrefixLength], issued.Record.Prefix);

        // **保存されている値のどこにも平文が無い**（接頭辞は識別用なので入ってよいが、全体は入らない）
        Assert.DoesNotContain(issued.Token, harness.Tokens.DumpAll(), StringComparison.Ordinal);

        // **一覧から平文もハッシュも取れない**
        var listed = Assert.Single(await harness.Service.ListAsync(owner.Id));
        Assert.DoesNotContain(
            typeof(AdminApiTokenRecord).GetProperties(),
            property => property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) && property.Name != "TokenId");
        Assert.Equal(issued.Record.TokenId, listed.TokenId);
    }

    [Fact]
    public async Task 平文はログにも例外にも文字列化にも出ない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var issued = await IssueAsync(harness, owner);
        await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback);
        await harness.Service.RevokeAsync(issued.Record.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner);

        var everything = string.Join('\n', harness.Log.Lines);
        Assert.NotEmpty(harness.Log.Lines);
        Assert.DoesNotContain(issued.Token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminApiTokenSecret.HashOf(issued.Token), everything, StringComparison.Ordinal);

        // 文字列化（誤ってログへ渡されても漏れない）
        Assert.DoesNotContain(issued.Token, issued.ToString(), StringComparison.Ordinal);
        var secret = AdminApiTokenSecret.Create();
        Assert.DoesNotContain(secret.Plain, secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, Reauth().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 値は毎回違い_接頭辞とハッシュの形が決まっている()
    {
        var first = AdminApiTokenSecret.Create();
        var second = AdminApiTokenSecret.Create();

        Assert.NotEqual(first.Plain, second.Plain);
        Assert.Matches("^qat_[0-9a-f]{8}_[A-Za-z0-9_-]{43}$", first.Plain);
        Assert.Matches("^[0-9a-f]{64}$", first.Hash);
        Assert.Equal(first.Hash, AdminApiTokenSecret.HashOf(first.Plain));
        Assert.True(AdminApiTokenSecret.LooksLikeToken(first.Plain));
        Assert.False(AdminApiTokenSecret.LooksLikeToken("Bearer abc"));
        Assert.False(AdminApiTokenSecret.LooksLikeToken(null));
    }

    [Fact]
    public async Task 機能が無効なら発行できず_発行済みのトークンも使えない()
    {
        var enabled = Create();
        var owner = await UserAsync(enabled, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(enabled, owner);

        // 同じ保存先で、機能だけ無効にした窓口
        var disabled = new AdminApiTokenService(
            enabled.Tokens,
            enabled.Users,
            enabled.UserService,
            new AdminApiTokenOptions { Enabled = false },
            enabled.Time,
            NullLogger<AdminApiTokenService>.Instance);

        var (outcome, none) = await disabled.IssueAsync(owner.Id, Request(), Reauth());
        Assert.Equal(AdminApiTokenIssueOutcome.FeatureDisabled, outcome);
        Assert.Null(none);

        var validation = await disabled.ValidateAsync(issued.Token, IPAddress.Loopback);
        Assert.Equal(AdminApiTokenRejection.FeatureDisabled, validation.Rejection);

        // **一覧と失効は使える**（漏れたトークンを止められなくならないように）
        Assert.Single(await disabled.ListAsync(owner.Id));
        Assert.NotNull(await disabled.RevokeAsync(
            issued.Record.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner));
    }

    [Fact]
    public void 機能は既定で無効()
    {
        Assert.False(new AdminApiTokenOptions().Enabled);
        Assert.False(AdminApiTokenOptions.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Enabled);
    }

    // ---- 期限（必須。既定 90 日・上限 365 日・無期限なし） -------------------------------

    [Fact]
    public async Task 期限を指定しなければ既定の90日になる()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var issued = await IssueAsync(harness, owner);

        Assert.Equal(harness.Time.GetUtcNow().UtcDateTime.AddDays(90), issued.Record.ExpiresAt);
        Assert.NotEqual(DateTime.MaxValue, issued.Record.ExpiresAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public async Task 上限までの期限は発行できる(int days)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var issued = await IssueAsync(harness, owner, Request(days: days));

        Assert.Equal(harness.Time.GetUtcNow().UtcDateTime.AddDays(days), issued.Record.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public async Task 無期限や上限を超える期限は発行できない(int days)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var (outcome, issued) = await harness.Service.IssueAsync(owner.Id, Request(days: days), Reauth());

        Assert.Equal(AdminApiTokenIssueOutcome.ExpiryInvalid, outcome);
        Assert.Null(issued);
        Assert.Empty(await harness.Service.ListAsync(owner.Id));
    }

    [Fact]
    public async Task 設定で上限を縮められるが_365日を超えては設定できない()
    {
        var harness = Create(new AdminApiTokenOptions { Enabled = true, MaxDays = 30, DefaultDays = 7 });
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        Assert.Equal(
            AdminApiTokenIssueOutcome.ExpiryInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(days: 31), Reauth())).Outcome);
        Assert.Equal(
            harness.Time.GetUtcNow().UtcDateTime.AddDays(7),
            (await IssueAsync(harness, owner)).Record.ExpiresAt);

        var tooLong = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminApiTokenOptions.ModeSetting] = "enabled",
                [AdminApiTokenOptions.MaxDaysSetting] = "366",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => AdminApiTokenOptions.FromConfiguration(tooLong));
    }

    [Theory]
    [InlineData("enabled", true)]
    [InlineData("Enabled", true)]
    [InlineData("disabled", false)]
    [InlineData("", false)]
    public void 機能の設定を読む(string value, bool expected)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdminApiTokenOptions.ModeSetting] = value })
            .Build();

        Assert.Equal(expected, AdminApiTokenOptions.FromConfiguration(configuration).Enabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    public void 知らない値は起動時に落とす(string value)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AdminApiTokenOptions.ModeSetting] = value })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AdminApiTokenOptions.FromConfiguration(configuration));
        Assert.Contains(AdminApiTokenOptions.ModeSetting, exception.Message, StringComparison.Ordinal);
    }

    // ---- スコープの制限 ---------------------------------------------------------------

    [Theory]
    [InlineData("surveys.read", true)]
    [InlineData("surveys.write", true)]
    [InlineData("surveys.publish", true)]
    [InlineData("surveys.delete", false)]
    [InlineData("lookups.manage", false)]
    [InlineData("users.read", false)]
    [InlineData("users.write", false)]
    [InlineData("users.resetTwoFactor", false)]
    [InlineData("settings.saml", false)]
    [InlineData("settings.pleasanterSso", false)]
    [InlineData("settings.manage", false)]
    [InlineData("maintenance.manage", false)]
    [InlineData("audit.read", false)]
    [InlineData("outbox.read", false)]
    [InlineData("notifications.read", false)]
    [InlineData("templates.read", false)]
    [InlineData("apiTokens.write", false)]
    [InlineData("", false)]
    public async Task 付けられるスコープは許可リストだけ(string scope, bool allowed)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var (outcome, issued) = await harness.Service.IssueAsync(owner.Id, Request(scopes: [scope]), Reauth());

        Assert.Equal(allowed, outcome == AdminApiTokenIssueOutcome.Issued);
        Assert.Equal(allowed, issued is not null);
        if (!allowed)
        {
            Assert.Equal(AdminApiTokenIssueOutcome.ScopesInvalid, outcome);
        }
    }

    [Fact]
    public void 許可リストは書き出し_取り込みの専用権限を足さず既存の権限だけ()
    {
        Assert.Equal(
            ["surveys.read", "surveys.write", "surveys.publish"],
            AdminApiTokenScopes.Allowed.ToArray());

        // **許可リストは、実在する権限だけ**（名前の打ち間違いで、権限にならないスコープを作らない）
        Assert.All(AdminApiTokenScopes.Allowed, scope => Assert.Contains(scope, AdminPermissions.All));
    }

    [Fact]
    public async Task スコープが空なら発行できない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        Assert.Equal(
            AdminApiTokenIssueOutcome.ScopesInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(scopes: []), Reauth())).Outcome);
        Assert.Equal(
            AdminApiTokenIssueOutcome.ScopesInvalid,
            (await harness.Service.IssueAsync(
                owner.Id, new AdminApiTokenIssueRequest("x", null, null, null), Reauth())).Outcome);
    }

    [Fact]
    public async Task 自分の権限を超えるスコープは付けられない()
    {
        var harness = Create();
        var editor = await UserAsync(harness, AdminRole.Editor, "editor");

        var (outcome, issued) = await harness.Service.IssueAsync(
            editor.Id, Request(scopes: [AdminPermissions.SurveysRead, AdminPermissions.SurveysPublish]), Reauth());

        Assert.Equal(AdminApiTokenIssueOutcome.ScopeExceedsRole, outcome);
        Assert.Null(issued);
    }

    // ---- 役割ごとの可否 ---------------------------------------------------------------

    [Theory]
    [InlineData(AdminRole.Administrator, true, "surveys.read,surveys.write,surveys.publish")]
    [InlineData(AdminRole.SurveyAdministrator, true, "surveys.read,surveys.write,surveys.publish")]
    [InlineData(AdminRole.Editor, true, "surveys.read,surveys.write")]
    [InlineData(AdminRole.UserAdministrator, false, "")]
    [InlineData(AdminRole.Auditor, false, "")]
    public async Task 発行できる役割と付けられるスコープは設計どおり(
        AdminRole role,
        bool canIssue,
        string expectedScopes)
    {
        var harness = Create();
        var user = await UserAsync(harness, role, "user");

        Assert.Equal(canIssue, AdminApiTokenScopes.CanIssue(AdminRoleMap.ToName(role)));
        Assert.Equal(
            expectedScopes.Split(',', StringSplitOptions.RemoveEmptyEntries),
            AdminApiTokenScopes.IssuableFor(AdminRoleMap.ToName(role)).ToArray());

        var (outcome, issued) = await harness.Service.IssueAsync(user.Id, Request(), Reauth());
        Assert.Equal(canIssue ? AdminApiTokenIssueOutcome.Issued : AdminApiTokenIssueOutcome.RoleNotAllowed, outcome);
        Assert.Equal(canIssue, issued is not null);
    }

    [Fact]
    public void 知らない役割には何も付けられない()
    {
        Assert.False(AdminApiTokenScopes.CanIssue("Unknown"));
        Assert.False(AdminApiTokenScopes.CanIssue(null));
        Assert.Empty(AdminApiTokenScopes.IssuableFor("99"));
    }

    // ---- 本人確認（パスワード・SSO の再ログイン） ---------------------------------------

    [Fact]
    public async Task パスワードが違えば発行できず_トークンも作られない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var (outcome, issued) = await harness.Service.IssueAsync(owner.Id, Request(), Reauth("wrong-password-123"));

        Assert.Equal(AdminApiTokenIssueOutcome.PasswordRejected, outcome);
        Assert.Null(issued);
        Assert.Empty(await harness.Service.ListAsync(owner.Id));

        var (empty, _) = await harness.Service.IssueAsync(owner.Id, Request(), new AdminApiTokenReauthentication.Password(null));
        Assert.Equal(AdminApiTokenIssueOutcome.PasswordRejected, empty);
    }

    [Fact]
    public async Task パスワードの失敗が続くと締め出される()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        // **ログインと同じ道で数える。** ここだけ免れると総当たりの抜け道になる
        var outcomes = new List<AdminApiTokenIssueOutcome>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            outcomes.Add((await harness.Service.IssueAsync(owner.Id, Request(), Reauth("wrong-password-123"))).Outcome);
        }

        Assert.Contains(AdminApiTokenIssueOutcome.LockedOut, outcomes);

        // 締め出し中は、正しいパスワードでも発行できない
        Assert.NotEqual(
            AdminApiTokenIssueOutcome.Issued,
            (await harness.Service.IssueAsync(owner.Id, Request(), Reauth())).Outcome);
    }

    [Fact]
    public async Task 入力の誤りではパスワードの試行を消費しない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        for (var attempt = 0; attempt < 12; attempt++)
        {
            Assert.Equal(
                AdminApiTokenIssueOutcome.ExpiryInvalid,
                (await harness.Service.IssueAsync(owner.Id, Request(days: 9999), Reauth("wrong-password-123"))).Outcome);
        }

        Assert.Equal(AdminApiTokenIssueOutcome.Issued, (await harness.Service.IssueAsync(owner.Id, Request(), Reauth())).Outcome);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(600, false)]
    public async Task SSOの管理者は直前の再ログインがなければ発行できない(int minutesAgo, bool allowed)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "sso-admin");
        var signedInAt = harness.Time.GetUtcNow().AddMinutes(-minutesAgo);

        var (outcome, issued) = await harness.Service.IssueAsync(
            owner.Id, Request(), new AdminApiTokenReauthentication.Relogin(signedInAt));

        Assert.Equal(allowed ? AdminApiTokenIssueOutcome.Issued : AdminApiTokenIssueOutcome.ReloginRequired, outcome);
        Assert.Equal(allowed, issued is not null);
    }

    [Fact]
    public async Task 未来の時刻の再ログインは信用しない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "sso-admin");

        var (outcome, _) = await harness.Service.IssueAsync(
            owner.Id, Request(), new AdminApiTokenReauthentication.Relogin(harness.Time.GetUtcNow().AddHours(1)));

        Assert.Equal(AdminApiTokenIssueOutcome.ReloginRequired, outcome);
    }

    // ---- SAML の強制再認証（Issue #1034） ----------------------------------------------

    [Fact]
    public async Task SAMLの管理者は強制再認証の許可がなければ発行できない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "saml-admin");

        var (outcome, issued) = await harness.Service.IssueAsync(
            owner.Id, Request(), new AdminApiTokenReauthentication.SamlForced(_ => Task.FromResult(false)));

        Assert.Equal(AdminApiTokenIssueOutcome.ReauthenticationRequired, outcome);
        Assert.Null(issued);
        Assert.Empty(harness.Tokens.StoredHashes);
    }

    [Fact]
    public async Task 強制再認証の許可は発行1回分で_同じ許可では2本目を作れない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "saml-admin");
        var grants = 1;
        var reauthentication = new AdminApiTokenReauthentication.SamlForced(
            _ => Task.FromResult(Interlocked.Decrement(ref grants) >= 0));

        var first = await harness.Service.IssueAsync(owner.Id, Request("first"), reauthentication);
        var second = await harness.Service.IssueAsync(owner.Id, Request("second"), reauthentication);

        Assert.Equal(AdminApiTokenIssueOutcome.Issued, first.Outcome);
        Assert.Equal(AdminApiTokenIssueOutcome.ReauthenticationRequired, second.Outcome);
        Assert.Single(harness.Tokens.StoredHashes);
    }

    [Fact]
    public async Task 入力の誤りでは強制再認証の許可を消費しない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "saml-admin");
        var consumed = 0;
        var reauthentication = new AdminApiTokenReauthentication.SamlForced(_ =>
        {
            consumed++;
            return Task.FromResult(true);
        });

        Assert.Equal(
            AdminApiTokenIssueOutcome.NameInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(""), reauthentication)).Outcome);
        Assert.Equal(
            AdminApiTokenIssueOutcome.ExpiryInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(days: 9999), reauthentication)).Outcome);
        Assert.Equal(0, consumed);

        Assert.Equal(
            AdminApiTokenIssueOutcome.Issued, (await harness.Service.IssueAsync(owner.Id, Request(), reauthentication)).Outcome);
        Assert.Equal(1, consumed);
    }

    // ---- 入力の検査 -------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public async Task 名前が空や制御文字なら発行できない(string name)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        Assert.Equal(
            AdminApiTokenIssueOutcome.NameInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(name: name), Reauth())).Outcome);
        Assert.Equal(
            AdminApiTokenIssueOutcome.NameInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(name: new string('a', 101)), Reauth())).Outcome);
    }

    [Fact]
    public async Task 接続元の許可リストは正規化して保存し_解釈できなければ断る()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");

        var issued = await IssueAsync(
            harness, owner, Request(ips: ["203.0.113.0/24, 198.51.100.7", "2001:db8::/32", "203.0.113.0/24"]));
        Assert.Equal(["203.0.113.0/24", "198.51.100.7/32", "2001:db8::/32"], issued.Record.IpAllowList);

        foreach (var bad in new[] { "not-an-ip", "10.0.0.0/33", "10.0.0.1/abc", "300.1.1.1" })
        {
            var outcome = (await harness.Service.IssueAsync(owner.Id, Request(ips: [bad]), Reauth())).Outcome;
            Assert.True(outcome == AdminApiTokenIssueOutcome.IpAllowListInvalid, $"{bad} -> {outcome}");
        }

        // ホスト部が付いた書き方は、ネットワークへ丸めて保存する（保存した形が一覧に出る）
        var rounded = await IssueAsync(harness, owner, Request(name: "rounded", ips: ["10.0.0.5/24"]));
        Assert.Equal(["10.0.0.0/24"], rounded.Record.IpAllowList);

        // **1 つでも解釈できなければ全体を断る。** 黙って捨てると、制限したつもりで開く
        Assert.Equal(
            AdminApiTokenIssueOutcome.IpAllowListInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(ips: ["10.0.0.1", "oops"]), Reauth())).Outcome);

        var tooMany = Enumerable.Range(1, AdminApiTokenOptions.MaxIpAllowListEntries + 1).Select(i => $"10.0.0.{i}").ToArray();
        Assert.Equal(
            AdminApiTokenIssueOutcome.IpAllowListInvalid,
            (await harness.Service.IssueAsync(owner.Id, Request(ips: tooMany), Reauth())).Outcome);
    }

    [Fact]
    public async Task いま使えるトークンには持ち主ごとの上限がある()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var other = await UserAsync(harness, AdminRole.Administrator, "other");

        for (var index = 0; index < AdminApiTokenOptions.MaxActivePerOwner; index++)
        {
            await IssueAsync(harness, owner, Request(name: $"k{index}"));
        }

        Assert.Equal(
            AdminApiTokenIssueOutcome.LimitReached,
            (await harness.Service.IssueAsync(owner.Id, Request(), Reauth())).Outcome);

        // 失効させれば、また発行できる。ほかの人の本数には影響しない
        var first = (await harness.Service.ListAsync(owner.Id))[0];
        await harness.Service.RevokeAsync(first.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner);
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, (await harness.Service.IssueAsync(owner.Id, Request(), Reauth())).Outcome);
        Assert.Equal(AdminApiTokenIssueOutcome.Issued, (await harness.Service.IssueAsync(other.Id, Request(), Reauth())).Outcome);
    }

    [Fact]
    public async Task 止められた持ち主は発行できない()
    {
        var harness = Create();
        var admin = await UserAsync(harness, AdminRole.Administrator, "admin");
        var target = await UserAsync(harness, AdminRole.Editor, "editor");
        await harness.Users.SetDisabledAsync(target.Id, true);

        Assert.Equal(
            AdminApiTokenIssueOutcome.OwnerUnavailable,
            (await harness.Service.IssueAsync(target.Id, Request(), Reauth())).Outcome);
        Assert.NotNull(admin);
    }

    // ---- 失効 -------------------------------------------------------------------------

    [Fact]
    public async Task 失効するとその場で使えなくなる()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner);
        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Succeeded);

        var revoked = await harness.Service.RevokeAsync(
            issued.Record.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner);

        Assert.NotNull(revoked);
        var validation = await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback);
        Assert.Equal(AdminApiTokenRejection.Revoked, validation.Rejection);
        var listed = Assert.Single(await harness.Service.ListAsync(owner.Id));
        Assert.NotNull(listed.RevokedAt);
        Assert.Equal(AdminApiTokenRevokeReasons.Owner, listed.RevokeReason);
        Assert.Equal(owner.Id, listed.RevokedBy);
    }

    [Fact]
    public async Task 他人のトークンは持ち主の組で絞るので失効できず_2回目は何も起きない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var stranger = await UserAsync(harness, AdminRole.Editor, "stranger");
        var issued = await IssueAsync(harness, owner);

        // 他人の識別子では失効しない（「無い」と同じ）
        Assert.Null(await harness.Service.RevokeAsync(
            issued.Record.TokenId, stranger.Id, stranger.Id, AdminApiTokenRevokeReasons.Owner));
        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Succeeded);

        Assert.NotNull(await harness.Service.RevokeAsync(
            issued.Record.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner));

        // すでに失効しているものは書き換えない（最初の理由と日時を残す）
        harness.Time.Advance(TimeSpan.FromHours(1));
        Assert.Null(await harness.Service.RevokeAsync(
            issued.Record.TokenId, owner.Id, stranger.Id, AdminApiTokenRevokeReasons.Administrator));
        var listed = Assert.Single(await harness.Service.ListAsync(owner.Id));
        Assert.Equal(AdminApiTokenRevokeReasons.Owner, listed.RevokeReason);
    }

    [Fact]
    public async Task 管理者が他人のトークンを失効できる_理由は管理者として残る()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Editor, "editor");
        var userAdmin = await UserAsync(harness, AdminRole.UserAdministrator, "user-admin");
        var issued = await IssueAsync(harness, owner);

        Assert.NotNull(await harness.Service.RevokeAsync(
            issued.Record.TokenId, owner.Id, userAdmin.Id, AdminApiTokenRevokeReasons.Administrator));

        var listed = Assert.Single(await harness.Service.ListAsync(owner.Id));
        Assert.Equal(AdminApiTokenRevokeReasons.Administrator, listed.RevokeReason);
        Assert.Equal(userAdmin.Id, listed.RevokedBy);
    }

    [Fact]
    public async Task まとめて失効すると_いま使えるものだけが対象になり接頭辞が返る()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var other = await UserAsync(harness, AdminRole.Administrator, "other");
        var first = await IssueAsync(harness, owner, Request(name: "a"));
        var second = await IssueAsync(harness, owner, Request(name: "b"));
        var keep = await IssueAsync(harness, other, Request(name: "c"));
        await harness.Service.RevokeAsync(first.Record.TokenId, owner.Id, owner.Id, AdminApiTokenRevokeReasons.Owner);

        var revoked = await harness.Service.RevokeAllAsync(owner.Id, owner.Id, AdminApiTokenRevokeReasons.PasswordChanged);

        Assert.Equal(second.Record.Prefix, Assert.Single(revoked).Prefix);
        Assert.False((await harness.Service.ValidateAsync(second.Token, IPAddress.Loopback)).Succeeded);
        Assert.True((await harness.Service.ValidateAsync(keep.Token, IPAddress.Loopback)).Succeeded);
        // 最初の失効の理由は書き換わらない
        Assert.Equal(
            AdminApiTokenRevokeReasons.Owner,
            (await harness.Service.ListAsync(owner.Id)).Single(token => token.TokenId == first.Record.TokenId).RevokeReason);
    }

    // ---- 自動失効（持ち主の停止・削除） ------------------------------------------------

    [Fact]
    public async Task 持ち主を止めるとトークンは失効し_有効へ戻しても生き返らない()
    {
        var harness = Create();
        var admin = await UserAsync(harness, AdminRole.Administrator, "admin");
        await harness.Users.RecordSuccessAsync(admin.Id);
        var target = await UserAsync(harness, AdminRole.Editor, "editor");
        var issued = await IssueAsync(harness, target);
        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Succeeded);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.UserService.SetDisabledAsync(admin.Id, target.Id, isDisabled: true));

        var listed = Assert.Single(await harness.Service.ListAsync(target.Id));
        Assert.Equal(AdminApiTokenRevokeReasons.OwnerDisabled, listed.RevokeReason);
        Assert.Equal(admin.Id, listed.RevokedBy);
        Assert.False((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Succeeded);

        // **有効へ戻しても、失効したトークンは生き返らない**（新しく発行し直す）
        await harness.UserService.SetDisabledAsync(admin.Id, target.Id, isDisabled: false);
        Assert.Equal(
            AdminApiTokenRejection.Revoked,
            (await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Rejection);
    }

    [Fact]
    public async Task 持ち主の状態は要求のたびに見る_停止も削除もその場で効く()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner);

        // ストアを直接止める（サービスの自動失効を通さない経路でも、検証が拒む）
        await harness.Users.SetDisabledAsync(owner.Id, true);
        var disabled = await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback);
        Assert.Equal(AdminApiTokenRejection.OwnerUnavailable, disabled.Rejection);
        Assert.Empty(disabled.EffectivePermissions);

        // 持ち主が居ない（削除）
        var orphan = new AdminApiTokenService(
            harness.Tokens,
            new FakeAdminUserStore(harness.Time),
            harness.UserService,
            new AdminApiTokenOptions { Enabled = true },
            harness.Time,
            NullLogger<AdminApiTokenService>.Instance);
        Assert.Equal(
            AdminApiTokenRejection.OwnerUnavailable,
            (await orphan.ValidateAsync(issued.Token, IPAddress.Loopback)).Rejection);
    }

    // ---- 検証（期限・実効の権限・接続元） -----------------------------------------------

    [Fact]
    public async Task 検証は持ち主の役割とスコープの積を返す()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(
            harness, owner, Request(scopes: [AdminPermissions.SurveysRead, AdminPermissions.SurveysWrite]));

        var validation = await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback);

        Assert.True(validation.Succeeded);
        Assert.Equal(owner.Id, validation.Owner!.Id);
        Assert.Equal(
            new[] { "surveys.read", "surveys.write" }.Order(),
            validation.EffectivePermissions.Order());

        // **付けていないスコープ（公開）は、特権管理者でも使えない**
        Assert.DoesNotContain(AdminPermissions.SurveysPublish, validation.EffectivePermissions);
        Assert.DoesNotContain(AdminPermissions.UsersWrite, validation.EffectivePermissions);
    }

    [Fact]
    public async Task 役割を降格すると_トークンもその場で弱くなる()
    {
        var harness = Create();
        var admin = await UserAsync(harness, AdminRole.Administrator, "admin");
        await harness.Users.RecordSuccessAsync(admin.Id);
        var owner = await UserAsync(harness, AdminRole.SurveyAdministrator, "survey-admin");
        var issued = await IssueAsync(
            harness, owner, Request(scopes: [AdminPermissions.SurveysRead, AdminPermissions.SurveysPublish]));
        Assert.Contains(
            AdminPermissions.SurveysPublish,
            (await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).EffectivePermissions);

        await harness.UserService.SetRoleAsync(admin.Id, owner.Id, AdminRole.Editor);

        // 失効はしないが、実効の権限だけが縮む（編集者は公開を持たない）
        var after = await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback);
        Assert.True(after.Succeeded);
        Assert.Equal([AdminPermissions.SurveysRead], after.EffectivePermissions.ToArray());

        // 発行できない役割へ降格すれば、何も持たない
        await harness.UserService.SetRoleAsync(admin.Id, owner.Id, AdminRole.Auditor);
        Assert.Empty((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).EffectivePermissions);
    }

    [Fact]
    public async Task 期限が来たら使えない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner, Request(days: 10));

        harness.Time.Advance(TimeSpan.FromDays(10).Subtract(TimeSpan.FromSeconds(1)));
        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Succeeded);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(
            AdminApiTokenRejection.Expired,
            (await harness.Service.ValidateAsync(issued.Token, IPAddress.Loopback)).Rejection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qat_")]
    [InlineData("qat_deadbeef_x")]
    [InlineData("Bearer qat_xxxxxxxxxxxx")]
    public async Task 形の違う値や無いトークンは見つからない(string? value)
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        await IssueAsync(harness, owner);

        var validation = await harness.Service.ValidateAsync(value, IPAddress.Loopback);

        Assert.Equal(AdminApiTokenRejection.NotFound, validation.Rejection);
        Assert.Null(validation.Owner);
        Assert.Empty(validation.EffectivePermissions);
    }

    [Fact]
    public async Task 値を1文字変えると別のトークンとして見つからない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner);
        var tampered = issued.Token[..^1] + (issued.Token[^1] == 'A' ? 'B' : 'A');

        Assert.Equal(
            AdminApiTokenRejection.NotFound,
            (await harness.Service.ValidateAsync(tampered, IPAddress.Loopback)).Rejection);
    }

    [Fact]
    public async Task 接続元の許可リストに入っていなければ使えない()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner, Request(ips: ["203.0.113.0/24"]));

        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("203.0.113.9"))).Succeeded);
        // IPv4 を IPv6 の形で受けても同じ扱い
        Assert.True((await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("::ffff:203.0.113.9"))).Succeeded);
        Assert.Equal(
            AdminApiTokenRejection.IpNotAllowed,
            (await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("198.51.100.1"))).Rejection);
        // 接続元が分からなければ、許可リストのあるトークンは使えない
        Assert.Equal(
            AdminApiTokenRejection.IpNotAllowed,
            (await harness.Service.ValidateAsync(issued.Token, null)).Rejection);

        // リストが空なら制限しない
        var open = await IssueAsync(harness, owner, Request(name: "open"));
        Assert.True((await harness.Service.ValidateAsync(open.Token, null)).Succeeded);
    }

    [Fact]
    public async Task 最後に使った日時は間引いて書く()
    {
        var harness = Create();
        var owner = await UserAsync(harness, AdminRole.Administrator, "admin");
        var issued = await IssueAsync(harness, owner);

        await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("203.0.113.9"));
        await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("203.0.113.9"));
        await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("203.0.113.9"));
        Assert.Equal(1, harness.Tokens.TouchWrites);

        harness.Time.Advance(AdminApiTokenService.LastUsedInterval + TimeSpan.FromSeconds(1));
        await harness.Service.ValidateAsync(issued.Token, IPAddress.Parse("203.0.113.9"));
        Assert.Equal(2, harness.Tokens.TouchWrites);

        var listed = Assert.Single(await harness.Service.ListAsync(owner.Id));
        Assert.Equal("203.0.113.9", listed.LastUsedIp);
        Assert.NotNull(listed.LastUsedAt);
    }
}
