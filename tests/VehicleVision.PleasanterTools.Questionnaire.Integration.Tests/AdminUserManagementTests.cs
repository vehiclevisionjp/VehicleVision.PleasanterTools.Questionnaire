using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>管理者の管理を 3 RDBMS で確かめる。</summary>
/// <remarks>
/// <para>
/// **環境変数 <c>QUESTIONNAIRE_INTEGRATION</c> を <c>1</c> にしたときだけ実行する。**
/// </para>
/// <para>
/// **見たいのは、締め出しを防ぐ判断が SQL 側で正しく効くこと。**
/// 「最後の管理者か」を 1 文の <c>UPDATE</c> の中で確かめており、
/// **その書き方（同じ表を副問い合わせで読む）は 3 者で通り方が違う**
/// （MySQL は派生表に包まないと ERROR 1093 になる）。
/// 記憶の上の試験（<c>AdminUserServiceTests</c>）では、ここが崩れても気付けない。
/// </para>
/// </remarks>
public class AdminUserManagementTests
{
    private const string Password = "long-enough-password";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("QUESTIONNAIRE_INTEGRATION") == "1";

    public static TheoryData<DatabaseProvider, string> Providers() =>
        DatabaseMigrationTests.Providers();

    private sealed record Harness(
        IAdminUserStore Users,
        IAdminInvitationStore Invitations,
        AdminUserService Service,
        AdminAuthenticator Authenticator,
        AdminAuthOptions Options,
        FakeTimeProvider Time);

    private static Harness Create(DatabaseProvider provider, string connectionString)
    {
        DatabaseMigrator.MigrateUp(provider, connectionString);
        var factory = new DbConnectionFactory(provider, connectionString);

        // **検証用の DB なので、毎回まっさらにしてから始める**
        using (var connection = factory.Create())
        {
            connection.Open();
            foreach (var table in new[] { "AdminInvitations", "AdminRecoveryCodes", "AdminUsers" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {SqlDialect.Quote(provider, table)}";
                command.ExecuteNonQuery();
            }
        }

        var users = new AdminUserStore(factory);
        var invitations = new AdminInvitationStore(factory);
        var hasher = new PasswordHasher();
        var options = new AdminAuthOptions();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));

        var authenticator = new AdminAuthenticator(
            users,
            hasher,
            new TotpService(),
            new SecretProtector(SecretProtector.GenerateKey()),
            options,
            time,
            NullLogger<AdminAuthenticator>.Instance);

        var service = new AdminUserService(
            users,
            invitations,
            authenticator,
            hasher,
            options,
            time,
            NullLogger<AdminUserService>.Instance);

        return new Harness(users, invitations, service, authenticator, options, time);
    }

    /// <summary>最初の管理者を作り、**一度ログインしたことにする。**</summary>
    /// <remarks>一度も入っていない管理者は頭数に数えない決まりのため。</remarks>
    private static async Task<AdminUser> FirstAdministratorAsync(Harness harness)
    {
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync("admin", Password))!;
        await harness.Users.RecordSuccessAsync(user.AdminUserId);
        return user;
    }

    private static async Task<Guid> InviteAndAcceptAsync(
        Harness harness,
        Guid actorId,
        string loginId,
        AdminRole role,
        bool signedIn = true)
    {
        var (outcome, invitation) = await harness.Service.InviteAsync(actorId, loginId, role);
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);

        var accepted = await harness.Service.AcceptInvitationAsync(invitation!.Token, Password);
        Assert.Equal(AdminUserOutcome.Succeeded, accepted.Outcome);

        if (signedIn)
        {
            await harness.Users.RecordSuccessAsync(invitation.AdminUserId);
        }

        return invitation.AdminUserId;
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 最後の管理者は止められない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        // **止めると誰も入れなくなる**
        Assert.False(await harness.Users.TryDisableAsync(admin.AdminUserId));

        var stored = await harness.Users.FindByIdAsync(admin.AdminUserId);
        Assert.False(stored!.IsDisabled);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 他に管理者が居れば止められる(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.AdminUserId, "admin2", AdminRole.Administrator);

        Assert.True(await harness.Users.TryDisableAsync(admin.AdminUserId));
        Assert.True((await harness.Users.FindByIdAsync(admin.AdminUserId))!.IsDisabled);

        // **残った 1 人は止められない**
        Assert.False(await harness.Users.TryDisableAsync(secondId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 一度もログインしていない管理者は頭数に入らない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);

        // 招待を受け取っただけで、まだ一度も入っていない管理者
        var secondId = await InviteAndAcceptAsync(
            harness, admin.AdminUserId, "admin2", AdminRole.Administrator, signedIn: false);

        // **当てにしない。** 本当に入れるかが分からないうちに最後の 1 人を止めさせない
        Assert.False(await harness.Users.TryDisableAsync(admin.AdminUserId));

        await harness.Users.RecordSuccessAsync(secondId);
        Assert.True(await harness.Users.TryDisableAsync(admin.AdminUserId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 最後の管理者は降格できない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        Assert.False(await harness.Users.TrySetRoleAsync(admin.AdminUserId, AdminRole.Editor));
        Assert.Equal(AdminRole.Administrator, (await harness.Users.FindByIdAsync(admin.AdminUserId))!.Role);

        // **昇格は誰も締め出さないので通る**
        Assert.True(await harness.Users.TrySetRoleAsync(editorId, AdminRole.Administrator));

        // 2 人になったので降格できる
        Assert.True(await harness.Users.TrySetRoleAsync(admin.AdminUserId, AdminRole.Editor));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 元からEditorなら降格の判定に掛からない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        // 管理者は 1 人しか居ないが、**この相手を降ろしても誰も締め出さない**
        Assert.True(await harness.Users.TrySetRoleAsync(editorId, AdminRole.Editor));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 招待は一度しか使い切れない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        var stored = await harness.Invitations
            .FindByTokenHashAsync(InvitationToken.HashOf(invitation!.Token));
        Assert.NotNull(stored);

        // **更新できた件数で判断している。** 同時に来ても 1 回しか通らない
        Assert.True(await harness.Invitations.TryConsumeAsync(stored.InvitationId));
        Assert.False(await harness.Invitations.TryConsumeAsync(stored.InvitationId));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 招待を出し直すと前のものは残らない(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var (_, first) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);
        await harness.Service.ReissueInvitationAsync(admin.AdminUserId, first!.AdminUserId);

        Assert.Null(await harness.Invitations.FindByTokenHashAsync(InvitationToken.HashOf(first.Token)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 使った招待は消さずに残る(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);
        await harness.Service.AcceptInvitationAsync(invitation!.Token, Password);

        // 出し直しても、**使われた事実は消さない**
        await harness.Service.ReissueInvitationAsync(admin.AdminUserId, invitation.AdminUserId);

        var used = await harness.Invitations.FindByTokenHashAsync(InvitationToken.HashOf(invitation.Token));
        Assert.NotNull(used);
        Assert.NotNull(used.UsedAt);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 期限切れと使用済みは招待中に数えない(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        var now = harness.Time.GetUtcNow().UtcDateTime;
        Assert.Contains(
            invitation!.AdminUserId,
            await harness.Invitations.ListPendingAdminUserIdsAsync(now));

        // 期限を過ぎたら出ない
        Assert.DoesNotContain(
            invitation.AdminUserId,
            await harness.Invitations.ListPendingAdminUserIdsAsync(
                now + harness.Options.InvitationLifetime + TimeSpan.FromMinutes(1)));

        // 受け取ったら出ない
        await harness.Service.AcceptInvitationAsync(invitation.Token, Password);
        Assert.DoesNotContain(
            invitation.AdminUserId,
            await harness.Invitations.ListPendingAdminUserIdsAsync(now));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 止めた相手の招待は取り消される(DatabaseProvider provider, string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        await harness.Service.SetDisabledAsync(
            admin.AdminUserId, invitation!.AdminUserId, isDisabled: true);

        Assert.Null(await harness.Invitations.FindByTokenHashAsync(InvitationToken.HashOf(invitation.Token)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task 招待を受け取ると自分で決めたパスワードで通る(
        DatabaseProvider provider,
        string connectionString)
    {
        if (!Enabled)
        {
            return;
        }

        var harness = Create(provider, connectionString);
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        var result = await harness.Authenticator.CheckPasswordAsync("editor", Password);

        // **既定は 2 要素を任意**にしたので、招待を受けた本人はそのまま通る（Issue #154）
        Assert.Equal(PasswordOutcome.SignedIn, result.Outcome);
    }
}
