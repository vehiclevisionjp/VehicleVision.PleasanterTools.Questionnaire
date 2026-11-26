using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>管理者の管理。**締め出し事故を作らないことを確かめる。**</summary>
/// <remarks>
/// DB を立てずに決まりだけを見る。3 RDBMS で同じ SQL が動くことは
/// <c>AdminUserManagementTests</c>（結合テスト）の受け持ち。
/// </remarks>
public class AdminUserServiceTests
{
    private const string Password = "long-enough-password";
    private const string AnotherPassword = "another-long-password";

    private sealed record Harness(
        FakeAdminUserStore Users,
        FakeAdminSessionStore Sessions,
        FakeAdminInvitationStore Invitations,
        AdminUserService Service,
        AdminAuthenticator Authenticator,
        AdminAuthOptions Options,
        FakeTimeProvider Time);

    private static Harness Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));
        var users = new FakeAdminUserStore(time);
        var sessions = new FakeAdminSessionStore(time);
        var invitations = new FakeAdminInvitationStore(time);
        var hasher = new PasswordHasher();
        var options = new AdminAuthOptions();

        var authenticator = new AdminAuthenticator(
            users,
            users,
            hasher,
            new TotpService(),
            options,
            time,
            NullLogger<AdminAuthenticator>.Instance);

        var service = new AdminUserService(
            users,
            users,
            sessions,
            invitations,
            new FakeAdminApiTokenStore(),
            authenticator,
            hasher,
            options,
            new AdminPasswordPolicy(new AdminPasswordPolicyOptions()),
            time,
            NullLogger<AdminUserService>.Instance);

        return new Harness(users, sessions, invitations, service, authenticator, options, time);
    }

    /// <summary>最初の管理者を作り、**一度ログインしたことにする。**</summary>
    /// <remarks>
    /// 一度も入っていない管理者は頭数に数えない決まりなので、
    /// **普通に運用されている状態**を作るには、ここでログインを済ませておく必要がある。
    /// </remarks>
    private static async Task<AdminUser> FirstAdministratorAsync(Harness harness, string loginId = "admin")
    {
        var user = (await harness.Authenticator.TryCreateFirstAdministratorAsync(loginId, Password))!;
        await harness.Users.RecordSuccessAsync(user.Id);
        return user;
    }

    /// <summary>招待して、受け取らせるところまで進める。</summary>
    /// <param name="signedIn">**一度ログインしたことにするか。**</param>
    private static async Task<Guid> InviteAndAcceptAsync(
        Harness harness,
        Guid actorId,
        string loginId,
        AdminRole role,
        string password = Password,
        bool signedIn = true)
    {
        var (outcome, invitation) = await harness.Service.InviteAsync(actorId, loginId, role);
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);

        var accepted = await harness.Service.AcceptInvitationAsync(invitation!.Token, password);
        Assert.Equal(AdminUserOutcome.Succeeded, accepted.Outcome);

        if (signedIn)
        {
            await harness.Users.RecordSuccessAsync(invitation.AdminUserId);
        }

        return invitation.AdminUserId;
    }

    // ---- 招待 --------------------------------------------------------------

    [Fact]
    public async Task 追加した管理者に既定のパスワードを配らない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var (outcome, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.NotNull(invitation);

        // **招待を受け取るまでは、どんな入力でも通らない。**
        // ありがちな「初期値」を試しても入れない
        foreach (var guess in new[] { "editor", invitation.Token, Password })
        {
            var attempt = await harness.Authenticator.CheckPasswordAsync("editor", guess);
            Assert.Equal(PasswordOutcome.Invalid, attempt.Outcome);
        }
    }

    [Fact]
    public async Task 招待を受け取ると自分で決めたパスワードで通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.Id, "editor", AdminRole.Editor, AnotherPassword);

        var result = await harness.Authenticator.CheckPasswordAsync("editor", AnotherPassword);

        // **パスワードは通る。** 既定の設定（2 要素は任意）ではそのまま入れる
        Assert.Equal(PasswordOutcome.SignedIn, result.Outcome);
    }

    [Fact]
    public async Task 招待は一度しか使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            (await harness.Service.AcceptInvitationAsync(invitation!.Token, Password)).Outcome);

        // **二度目は通さない。** 拾われた招待でもう一度パスワードを決められては困る
        Assert.Equal(
            AdminUserOutcome.InvitationInvalid,
            (await harness.Service.AcceptInvitationAsync(invitation.Token, AnotherPassword)).Outcome);
    }

    [Fact]
    public async Task 期限が切れた招待は使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        harness.Time.Advance(harness.Options.InvitationLifetime + TimeSpan.FromMinutes(1));

        Assert.Equal(
            AdminUserOutcome.InvitationInvalid,
            (await harness.Service.AcceptInvitationAsync(invitation!.Token, Password)).Outcome);
    }

    [Fact]
    public async Task 招待を出し直すと前の招待は使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, first) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        var (outcome, second) = await harness.Service
            .ReissueInvitationAsync(admin.Id, first!.AdminUserId);
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);

        // **前の紙を残さない。** 残すと、出し直した意味が無い
        Assert.Equal(
            AdminUserOutcome.InvitationInvalid,
            (await harness.Service.AcceptInvitationAsync(first.Token, Password)).Outcome);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            (await harness.Service.AcceptInvitationAsync(second!.Token, Password)).Outcome);
    }

    [Fact]
    public async Task 止めた管理者宛ての招待は取り消される()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(
                admin.Id, invitation!.AdminUserId, isDisabled: true));

        // **止めたのにパスワードを決められては困る**
        Assert.Equal(
            AdminUserOutcome.InvitationInvalid,
            (await harness.Service.AcceptInvitationAsync(invitation.Token, Password)).Outcome);
    }

    [Fact]
    public async Task 短いパスワードでは招待を受け取れない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.WeakPassword,
            (await harness.Service.AcceptInvitationAsync(invitation!.Token, "short")).Outcome);
    }

    [Fact]
    public async Task 同じログインIDは二度使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await harness.Service.InviteAsync(admin.Id, "editor", AdminRole.Editor);

        var (outcome, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.DuplicateLoginId, outcome);
        Assert.Null(invitation);
    }

    [Fact]
    public async Task 同じログインIDは大文字小文字が違っても二度使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await harness.Service.InviteAsync(admin.Id, "Editor@Example.test", AdminRole.Editor);

        var (outcome, _) = await harness.Service.InviteAsync(admin.Id, "editor@example.TEST", AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.DuplicateLoginId, outcome);
    }

    // ---- 締め出し事故を防ぐ ------------------------------------------------

    [Fact]
    public async Task 最後の管理者は無効化できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.Id, "editor", AdminRole.Editor);

        // **Editor が何人居ても、管理者が居なくなれば誰も足せない**
        var outcome = await harness.Service.SetDisabledAsync(editorId, admin.Id, isDisabled: true);

        Assert.Equal(AdminUserOutcome.LastAdministrator, outcome);
        Assert.False((await harness.Users.FindByIdAsync(admin.Id))!.IsDisabled);
    }

    [Fact]
    public async Task 最後の管理者は降格できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.Id, "editor", AdminRole.Editor);

        var outcome = await harness.Service.SetRoleAsync(editorId, admin.Id, AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.LastAdministrator, outcome);
        Assert.Equal(nameof(AdminRole.Administrator), (await harness.Users.FindByIdAsync(admin.Id))!.Role);
    }

    [Fact]
    public async Task 止まっている管理者は最後の一人の数に入らない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.Id, "admin2", AdminRole.Administrator);

        // 2 人目を止める。**まだ 1 人目が居るので通る**
        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(admin.Id, secondId, isDisabled: true));

        // **止まっている 2 人目は当てにならない。** 1 人目は止められない
        Assert.Equal(
            AdminUserOutcome.LastAdministrator,
            await harness.Service.SetDisabledAsync(secondId, admin.Id, isDisabled: true));
    }

    [Fact]
    public async Task 一度もログインしていない管理者は頭数に入らない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // 招待を受け取っただけで、まだ一度も入っていない管理者
        var secondId = await InviteAndAcceptAsync(
            harness, admin.Id, "admin2", AdminRole.Administrator, signedIn: false);

        // **当てにしない。** 本当に入れるかが分からないうちに最後の 1 人を止めさせない
        Assert.Equal(
            AdminUserOutcome.LastAdministrator,
            await harness.Service.SetDisabledAsync(secondId, admin.Id, isDisabled: true));

        // 一度でも入れたなら、頼りにしてよい
        await harness.Users.RecordSuccessAsync(secondId);
        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(secondId, admin.Id, isDisabled: true));
    }

    [Fact]
    public async Task 他に管理者が居れば無効化できる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.Id, "admin2", AdminRole.Administrator);

        var outcome = await harness.Service.SetDisabledAsync(secondId, admin.Id, isDisabled: true);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.True((await harness.Users.FindByIdAsync(admin.Id))!.IsDisabled);

        // **有効へ戻せる**
        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(secondId, admin.Id, isDisabled: false));
        Assert.False((await harness.Users.FindByIdAsync(admin.Id))!.IsDisabled);
    }

    [Fact]
    public async Task 自分自身は無効化できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.Id, "admin2", AdminRole.Administrator);

        // 他に管理者が居ても、**自分は止めさせない**（自分では戻せない）
        var outcome = await harness.Service
            .SetDisabledAsync(admin.Id, admin.Id, isDisabled: true);

        Assert.Equal(AdminUserOutcome.SelfNotAllowed, outcome);
    }

    [Fact]
    public async Task 自分の役割は変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.Id, "admin2", AdminRole.Administrator);

        var outcome = await harness.Service
            .SetRoleAsync(admin.Id, admin.Id, AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.SelfNotAllowed, outcome);
    }

    [Fact]
    public async Task 役割を変えると相手のセッションはすべて失効し他人のものは残る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.Id, "admin2", AdminRole.Administrator);
        await harness.Sessions.CreateAsync(secondId, AdminSessionKinds.Session, TimeSpan.FromHours(8));
        await harness.Sessions.CreateAsync(secondId, AdminSessionKinds.Pending, TimeSpan.FromMinutes(5));
        var adminSession = await harness.Sessions.CreateAsync(admin.Id, AdminSessionKinds.Session, TimeSpan.FromHours(8));

        var outcome = await harness.Service.SetRoleAsync(admin.Id, secondId, AdminRole.Editor);

        // **降格した相手が、古い役割のまま操作を続けられない**（cookie はセッション ID だけで、役割を持たない）
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.Empty(await harness.Sessions.ListForUserAsync(secondId));
        Assert.Equal(1, harness.Sessions.Count);
        Assert.NotNull(await harness.Sessions.FindAsync(adminSession));
    }

    [Fact]
    public async Task 役割を変えられなかったときはセッションを失効させない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.Id, "editor", AdminRole.Editor);
        var adminSession = await harness.Sessions.CreateAsync(admin.Id, AdminSessionKinds.Session, TimeSpan.FromHours(8));

        // 最後の Administrator は降格できない
        Assert.Equal(
            AdminUserOutcome.LastAdministrator,
            await harness.Service.SetRoleAsync(editorId, admin.Id, AdminRole.Editor));
        Assert.NotNull(await harness.Sessions.FindAsync(adminSession));
    }

    [Fact]
    public async Task 昇格は最後の一人でも通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.Id, "editor", AdminRole.Editor);

        var outcome = await harness.Service
            .SetRoleAsync(admin.Id, editorId, AdminRole.Administrator);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.Equal(nameof(AdminRole.Administrator), (await harness.Users.FindByIdAsync(editorId))!.Role);
    }

    // ---- 一覧 --------------------------------------------------------------

    [Fact]
    public async Task 一覧に招待中と最終ログイン日時が出る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.Id, "editor", AdminRole.Editor);

        var invited = (await harness.Service.ListAsync())
            .Single(user => user.AdminUserId == invitation!.AdminUserId);

        Assert.True(invited.InvitationPending);
        // **一度も入っていないので空。** 止め忘れの手掛かりになる列
        Assert.Null(invited.LastLoginAt);
        Assert.False(invited.HasTotp);

        await harness.Service.AcceptInvitationAsync(invitation!.Token, Password);
        await harness.Users.RecordSuccessAsync(invitation.AdminUserId);

        var accepted = (await harness.Service.ListAsync())
            .Single(user => user.AdminUserId == invitation.AdminUserId);

        Assert.False(accepted.InvitationPending);
        Assert.NotNull(accepted.LastLoginAt);
    }

    // ---- パスワード設定済みの印（Issue #1086） --------------------------------------

    [Fact]
    public async Task 招待で追加した管理者は受け取るとパスワード設定済みになる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        Assert.True(await harness.Users.GetPasswordSetAsync(admin.Id));

        var (_, invitation) = await harness.Service.InviteAsync(admin.Id, "editor", AdminRole.Editor);
        Assert.True((await harness.Service.ListAsync()).Single(user => user.AdminUserId == invitation!.AdminUserId).PasswordSet);

        await harness.Service.AcceptInvitationAsync(invitation!.Token, Password);

        Assert.True(await harness.Users.GetPasswordSetAsync(invitation.AdminUserId));
    }

    [Fact]
    public async Task パスワードなしのJIT管理者へ招待を出し直して受け取ると設定済みへ直る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var jit = (await harness.Users.CreateAsync("jit", new PasswordHasher().Hash(Guid.NewGuid().ToString()), "Editor", ""))!;
        await harness.Users.SetPasswordSetAsync(jit.Id, false);

        var (outcome, invitation) = await harness.Service.ReissueInvitationAsync(admin.Id, jit.Id);
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        // 受け取るまでは未設定のまま
        Assert.False(await harness.Users.GetPasswordSetAsync(jit.Id));

        await harness.Service.AcceptInvitationAsync(invitation!.Token, Password);

        Assert.True(await harness.Users.GetPasswordSetAsync(jit.Id));
    }

    [Fact]
    public async Task 受け取れなかった招待では印を変えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var jit = (await harness.Users.CreateAsync("jit", "hash", "Editor", ""))!;
        await harness.Users.SetPasswordSetAsync(jit.Id, false);
        var (_, invitation) = await harness.Service.ReissueInvitationAsync(admin.Id, jit.Id);

        // 条件を満たさないパスワードは、招待を使い切る前に断る
        Assert.Equal(AdminUserOutcome.WeakPassword, (await harness.Service.AcceptInvitationAsync(invitation!.Token, "x")).Outcome);

        Assert.False(await harness.Users.GetPasswordSetAsync(jit.Id));
    }

    [Fact]
    public async Task パスワードを変えると設定済みになる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await harness.Users.SetPasswordSetAsync(admin.Id, false);

        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.ChangeOwnPasswordAsync(admin.Id, Password, AnotherPassword));

        Assert.True(await harness.Users.GetPasswordSetAsync(admin.Id));
    }

    [Fact]
    public async Task パスワードを変えられなかったときは印を変えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await harness.Users.SetPasswordSetAsync(admin.Id, false);

        Assert.Equal(AdminUserOutcome.PasswordRejected, await harness.Service.ChangeOwnPasswordAsync(admin.Id, "wrong-password-x", AnotherPassword));

        Assert.False(await harness.Users.GetPasswordSetAsync(admin.Id));
    }

    [Fact]
    public async Task 他の管理者をパスワードなしにでき_一覧へ出て_戻せる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var target = await InviteAndAcceptAsync(harness, admin.Id, "sso-user", AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.SetSsoOnlyAsync(admin.Id, target, true));
        Assert.False(await harness.Users.GetPasswordSetAsync(target));
        var listed = await harness.Service.ListAsync();
        Assert.False(listed.Single(user => user.AdminUserId == target).PasswordSet);
        Assert.True(listed.Single(user => user.AdminUserId == admin.Id).PasswordSet);

        // 同じ向きをもう一度行っても何も起きない
        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.SetSsoOnlyAsync(admin.Id, target, true));

        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.SetSsoOnlyAsync(admin.Id, target, false));
        Assert.True(await harness.Users.GetPasswordSetAsync(target));
    }

    [Fact]
    public async Task 印の切り替えはログインの成否にもセッションにも触れない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var target = await InviteAndAcceptAsync(harness, admin.Id, "pw-user", AdminRole.Editor);
        var session = await harness.Sessions.CreateAsync(target, AdminSessionKinds.Session, TimeSpan.FromHours(1));

        // **パスワードで入っている管理者へ付けても、締め出さない**
        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.SetSsoOnlyAsync(admin.Id, target, true));

        Assert.Equal(PasswordOutcome.SignedIn, (await harness.Authenticator.CheckPasswordAsync("pw-user", Password)).Outcome);
        Assert.NotNull(await harness.Sessions.FindAsync(session));
        Assert.False((await harness.Users.FindByIdAsync(target))!.IsDisabled);
    }

    [Fact]
    public async Task 自分をパスワードありへ戻す操作は通さず_自分をパスワードなしにする向きは通す()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // 安全側（最後のパスキーを消せなくなる向き）は通す
        Assert.Equal(AdminUserOutcome.Succeeded, await harness.Service.SetSsoOnlyAsync(admin.Id, admin.Id, true));
        Assert.False(await harness.Users.GetPasswordSetAsync(admin.Id));

        // 知らない印を自分で立てて締め出される道は作らない
        Assert.Equal(AdminUserOutcome.SelfNotAllowed, await harness.Service.SetSsoOnlyAsync(admin.Id, admin.Id, false));
        Assert.False(await harness.Users.GetPasswordSetAsync(admin.Id));
    }

    [Fact]
    public async Task 居ない管理者の印は切り替えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        Assert.Equal(AdminUserOutcome.NotFound, await harness.Service.SetSsoOnlyAsync(admin.Id, Guid.NewGuid(), true));
    }

    // ---- 自分のパスワード ------------------------------------------------------

    [Fact]
    public async Task パスワードを変えると新しい方で通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var outcome = await harness.Service
            .ChangeOwnPasswordAsync(admin.Id, Password, AnotherPassword);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.Equal(
            PasswordOutcome.SignedIn,
            (await harness.Authenticator.CheckPasswordAsync("admin", AnotherPassword)).Outcome);
        Assert.Equal(
            PasswordOutcome.Invalid,
            (await harness.Authenticator.CheckPasswordAsync("admin", Password)).Outcome);
    }

    [Fact]
    public async Task 今のパスワードが違えば変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var outcome = await harness.Service
            .ChangeOwnPasswordAsync(admin.Id, "wrong-password", AnotherPassword);

        Assert.Equal(AdminUserOutcome.PasswordRejected, outcome);
        Assert.Equal(
            PasswordOutcome.SignedIn,
            (await harness.Authenticator.CheckPasswordAsync("admin", Password)).Outcome);
    }

    [Fact]
    public async Task 短いパスワードには変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        Assert.Equal(
            AdminUserOutcome.WeakPassword,
            await harness.Service.ChangeOwnPasswordAsync(admin.Id, Password, "short"));
    }

    [Fact]
    public async Task 今と同じパスワードには変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        Assert.Equal(
            AdminUserOutcome.InvalidInput,
            await harness.Service.ChangeOwnPasswordAsync(admin.Id, Password, Password));
    }

    [Fact]
    public async Task パスワードの変更でも失敗が続けば締め出す()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // **ここを免れると、総当たりの抜け道になる**
        for (var attempt = 0; attempt < harness.Options.MaxFailedAttempts; attempt++)
        {
            Assert.Equal(
                AdminUserOutcome.PasswordRejected,
                await harness.Service.ChangeOwnPasswordAsync(
                    admin.Id, "wrong-password", AnotherPassword));
        }

        Assert.Equal(
            AdminUserOutcome.LockedOut,
            await harness.Service.ChangeOwnPasswordAsync(admin.Id, Password, AnotherPassword));
    }

    [Fact]
    public async Task 二要素を登録し直す前にパスワードを確かめる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // **画面を奪われただけで 2 要素を差し替えられないようにする関門**
        Assert.Equal(
            AdminUserOutcome.PasswordRejected,
            await harness.Service.ConfirmOwnPasswordAsync(admin.Id, "wrong-password"));

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.ConfirmOwnPasswordAsync(admin.Id, Password));
    }
}
