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
        FakeAdminInvitationStore Invitations,
        AdminUserService Service,
        AdminAuthenticator Authenticator,
        AdminAuthOptions Options,
        FakeTimeProvider Time);

    private static Harness Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));
        var users = new FakeAdminUserStore(time);
        var invitations = new FakeAdminInvitationStore(time);
        var hasher = new PasswordHasher();
        var options = new AdminAuthOptions();

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

    /// <summary>最初の管理者を作る。</summary>
    private static async Task<AdminUser> FirstAdministratorAsync(Harness harness, string loginId = "admin") =>
        (await harness.Authenticator.TryCreateFirstAdministratorAsync(loginId, Password))!;

    /// <summary>招待して、受け取らせるところまで進める。</summary>
    private static async Task<Guid> InviteAndAcceptAsync(
        Harness harness,
        Guid actorId,
        string loginId,
        AdminRole role,
        string password = Password)
    {
        var (outcome, invitation) = await harness.Service.InviteAsync(actorId, loginId, role);
        Assert.Equal(AdminUserOutcome.Succeeded, outcome);

        var accepted = await harness.Service.AcceptInvitationAsync(invitation!.Token, password);
        Assert.Equal(AdminUserOutcome.Succeeded, accepted.Outcome);

        return invitation.AdminUserId;
    }

    // ---- 招待 --------------------------------------------------------------

    [Fact]
    public async Task 追加した管理者に既定の合言葉を配らない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var (outcome, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

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
    public async Task 招待を受け取ると自分で決めた合言葉で通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor, AnotherPassword);

        var result = await harness.Authenticator.CheckPasswordAsync("editor", AnotherPassword);

        // 合言葉は通り、次は 2 要素の登録を求められる
        Assert.Equal(PasswordOutcome.NeedsTotpEnrollment, result.Outcome);
    }

    [Fact]
    public async Task 招待は一度しか使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            (await harness.Service.AcceptInvitationAsync(invitation!.Token, Password)).Outcome);

        // **二度目は通さない。** 拾われた招待でもう一度合言葉を決められては困る
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
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

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
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        var (outcome, second) = await harness.Service
            .ReissueInvitationAsync(admin.AdminUserId, first!.AdminUserId);
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
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(
                admin.AdminUserId, invitation!.AdminUserId, isDisabled: true));

        // **止めたのに合言葉を決められては困る**
        Assert.Equal(
            AdminUserOutcome.InvitationInvalid,
            (await harness.Service.AcceptInvitationAsync(invitation.Token, Password)).Outcome);
    }

    [Fact]
    public async Task 短い合言葉では招待を受け取れない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        Assert.Equal(
            AdminUserOutcome.WeakPassword,
            (await harness.Service.AcceptInvitationAsync(invitation!.Token, "short")).Outcome);
    }

    [Fact]
    public async Task 同じログインIDは二度使えない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await harness.Service.InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        var (outcome, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.DuplicateLoginId, outcome);
        Assert.Null(invitation);
    }

    // ---- 締め出し事故を防ぐ ------------------------------------------------

    [Fact]
    public async Task 最後の管理者は無効化できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        // **Editor が何人居ても、管理者が居なくなれば誰も足せない**
        var outcome = await harness.Service.SetDisabledAsync(editorId, admin.AdminUserId, isDisabled: true);

        Assert.Equal(AdminUserOutcome.LastAdministrator, outcome);
        Assert.False((await harness.Users.FindByIdAsync(admin.AdminUserId))!.IsDisabled);
    }

    [Fact]
    public async Task 最後の管理者は降格できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        var outcome = await harness.Service.SetRoleAsync(editorId, admin.AdminUserId, AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.LastAdministrator, outcome);
        Assert.Equal(AdminRole.Administrator, (await harness.Users.FindByIdAsync(admin.AdminUserId))!.Role);
    }

    [Fact]
    public async Task 止まっている管理者は最後の一人の数に入らない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.AdminUserId, "admin2", AdminRole.Administrator);

        // 2 人目を止める。**まだ 1 人目が居るので通る**
        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(admin.AdminUserId, secondId, isDisabled: true));

        // **止まっている 2 人目は当てにならない。** 1 人目は止められない
        Assert.Equal(
            AdminUserOutcome.LastAdministrator,
            await harness.Service.SetDisabledAsync(secondId, admin.AdminUserId, isDisabled: true));
    }

    [Fact]
    public async Task 他に管理者が居れば無効化できる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var secondId = await InviteAndAcceptAsync(
            harness, admin.AdminUserId, "admin2", AdminRole.Administrator);

        var outcome = await harness.Service.SetDisabledAsync(secondId, admin.AdminUserId, isDisabled: true);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.True((await harness.Users.FindByIdAsync(admin.AdminUserId))!.IsDisabled);

        // **有効へ戻せる**
        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.SetDisabledAsync(secondId, admin.AdminUserId, isDisabled: false));
        Assert.False((await harness.Users.FindByIdAsync(admin.AdminUserId))!.IsDisabled);
    }

    [Fact]
    public async Task 自分自身は無効化できない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.AdminUserId, "admin2", AdminRole.Administrator);

        // 他に管理者が居ても、**自分は止めさせない**（自分では戻せない）
        var outcome = await harness.Service
            .SetDisabledAsync(admin.AdminUserId, admin.AdminUserId, isDisabled: true);

        Assert.Equal(AdminUserOutcome.SelfNotAllowed, outcome);
    }

    [Fact]
    public async Task 自分の役割は変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        await InviteAndAcceptAsync(harness, admin.AdminUserId, "admin2", AdminRole.Administrator);

        var outcome = await harness.Service
            .SetRoleAsync(admin.AdminUserId, admin.AdminUserId, AdminRole.Editor);

        Assert.Equal(AdminUserOutcome.SelfNotAllowed, outcome);
    }

    [Fact]
    public async Task 昇格は最後の一人でも通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var editorId = await InviteAndAcceptAsync(harness, admin.AdminUserId, "editor", AdminRole.Editor);

        var outcome = await harness.Service
            .SetRoleAsync(admin.AdminUserId, editorId, AdminRole.Administrator);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.Equal(AdminRole.Administrator, (await harness.Users.FindByIdAsync(editorId))!.Role);
    }

    // ---- 一覧 --------------------------------------------------------------

    [Fact]
    public async Task 一覧に招待中と最終ログイン日時が出る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);
        var (_, invitation) = await harness.Service
            .InviteAsync(admin.AdminUserId, "editor", AdminRole.Editor);

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

    // ---- 自分の合言葉 ------------------------------------------------------

    [Fact]
    public async Task 合言葉を変えると新しい方で通る()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var outcome = await harness.Service
            .ChangeOwnPasswordAsync(admin.AdminUserId, Password, AnotherPassword);

        Assert.Equal(AdminUserOutcome.Succeeded, outcome);
        Assert.Equal(
            PasswordOutcome.NeedsTotpEnrollment,
            (await harness.Authenticator.CheckPasswordAsync("admin", AnotherPassword)).Outcome);
        Assert.Equal(
            PasswordOutcome.Invalid,
            (await harness.Authenticator.CheckPasswordAsync("admin", Password)).Outcome);
    }

    [Fact]
    public async Task 今の合言葉が違えば変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        var outcome = await harness.Service
            .ChangeOwnPasswordAsync(admin.AdminUserId, "wrong-password", AnotherPassword);

        Assert.Equal(AdminUserOutcome.PasswordRejected, outcome);
        Assert.Equal(
            PasswordOutcome.NeedsTotpEnrollment,
            (await harness.Authenticator.CheckPasswordAsync("admin", Password)).Outcome);
    }

    [Fact]
    public async Task 短い合言葉には変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        Assert.Equal(
            AdminUserOutcome.WeakPassword,
            await harness.Service.ChangeOwnPasswordAsync(admin.AdminUserId, Password, "short"));
    }

    [Fact]
    public async Task 今と同じ合言葉には変えられない()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        Assert.Equal(
            AdminUserOutcome.InvalidInput,
            await harness.Service.ChangeOwnPasswordAsync(admin.AdminUserId, Password, Password));
    }

    [Fact]
    public async Task 合言葉の変更でも失敗が続けば締め出す()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // **ここを免れると、総当たりの抜け道になる**
        for (var attempt = 0; attempt < harness.Options.MaxFailedAttempts; attempt++)
        {
            Assert.Equal(
                AdminUserOutcome.PasswordRejected,
                await harness.Service.ChangeOwnPasswordAsync(
                    admin.AdminUserId, "wrong-password", AnotherPassword));
        }

        Assert.Equal(
            AdminUserOutcome.LockedOut,
            await harness.Service.ChangeOwnPasswordAsync(admin.AdminUserId, Password, AnotherPassword));
    }

    [Fact]
    public async Task 二要素を登録し直す前に合言葉を確かめる()
    {
        var harness = Create();
        var admin = await FirstAdministratorAsync(harness);

        // **画面を奪われただけで 2 要素を差し替えられないようにする関門**
        Assert.Equal(
            AdminUserOutcome.PasswordRejected,
            await harness.Service.ConfirmOwnPasswordAsync(admin.AdminUserId, "wrong-password"));

        Assert.Equal(
            AdminUserOutcome.Succeeded,
            await harness.Service.ConfirmOwnPasswordAsync(admin.AdminUserId, Password));
    }
}
