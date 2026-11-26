using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者の管理の結果。</summary>
/// <remarks>**文言は入口側で決める。** ここは何が起きたかだけを返す。</remarks>
public enum AdminUserOutcome
{
    Succeeded,

    /// <summary>その管理者が居ない。</summary>
    NotFound,

    /// <summary>そのログイン ID は使われている。</summary>
    DuplicateLoginId,

    /// <summary>入力が足りない。</summary>
    InvalidInput,

    /// <summary>パスワードが条件を満たしていない。</summary>
    WeakPassword,

    /// <summary>自分自身には行えない操作。</summary>
    SelfNotAllowed,

    /// <summary>
    /// **他に入れる <see cref="AdminRole.Administrator"/> が居ないので行えない。**
    /// 「入れる」は、有効かつ一度でもログインしたことがあること。
    /// </summary>
    LastAdministrator,

    /// <summary>招待が無い・期限切れ・使用済み。**理由は区別して返さない。**</summary>
    InvitationInvalid,

    /// <summary>今のパスワードが違う。</summary>
    PasswordRejected,

    /// <summary>締め出し中。</summary>
    LockedOut,
}

/// <summary>一覧に出す 1 人分。**秘密は 1 つも含めない。**</summary>
public sealed record AdminUserSummary
{
    public required Guid AdminUserId { get; init; }
    public required string LoginId { get; init; }

    /// <summary>役割の名前（<see cref="AdminRole"/> のメンバー名。Core の表が持つ文字列のまま）。</summary>
    public required string Role { get; init; }
    public required bool IsDisabled { get; init; }

    /// <summary>2 要素の登録が済んでいるか。</summary>
    public required bool HasTotp { get; init; }

    /// <summary>招待をまだ受け取っていないか。</summary>
    public required bool InvitationPending { get; init; }

    /// <summary>
    /// 本人がパスワードを知っている（設定した）か。<b>偽は「パスワードなし（SSO のみ）」</b>（Issue #1086）。
    /// ログインの成否ではなく、パスキーの最後の 1 件を消してよいかの判定に使う印。
    /// </summary>
    public bool PasswordSet { get; init; } = true;

    /// <summary>**止め忘れを見つける唯一の手掛かり**（<c>_documents/データモデル設計.md</c> 2.6）。</summary>
    public DateTime? LastLoginAt { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>発行した招待。**トークンを返せるのはこの時だけ。**</summary>
public sealed record IssuedInvitation(Guid AdminUserId, string Token, DateTime ExpiresAt);

/// <summary>管理者の追加・無効化・役割の変更。</summary>
/// <remarks>
/// <para>
/// **締め出し事故を作らないことが、この型の一番の仕事**
/// （<c>_documents/非機能設計.md</c> 1 章）。守っているのは 3 つ。
/// </para>
/// <list type="bullet">
///   <item>
///     **最後の <see cref="AdminRole.Administrator"/> を止めない・降格させない。** 誰も入れなくなる。
///     **一度もログインしていない管理者は頭数に入れない**（招待しただけの相手を当てにしない）
///   </item>
///   <item>**自分自身を止めさせない・自分の役割を変えさせない。** 手が滑ったときに戻せない</item>
///   <item>**既定のパスワードを配らない。** 期限付きで 1 回しか使えない招待を渡す</item>
/// </list>
/// <para>
/// **他人に触れるのは <see cref="AdminRole.Administrator"/> だけ**という切り分けは、
/// 入口側の認可で行う（<c>AdminUserEndpoints</c>）。
/// </para>
/// </remarks>
public sealed class AdminUserService(
    IAdminUserStore store,
    IAdminUserSettingsStore settings,
    IAdminSessionStore sessions,
    IAdminInvitationStore invitations,
    IAdminApiTokenStore apiTokens,
    AdminAuthenticator authenticator,
    PasswordHasher hasher,
    AdminAuthOptions options,
    AdminPasswordPolicy policy,
    TimeProvider timeProvider,
    ILogger<AdminUserService> logger)
{
    /// <summary>管理者の一覧。</summary>
    public async Task<IReadOnlyList<AdminUserSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var pending = (await invitations
            .ListPendingAdminUserIdsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var ssoOnlyIds = await settings.ListSsoOnlyAdminUserIdsAsync(cancellationToken).ConfigureAwait(false);

        return users.Select(user => new AdminUserSummary
        {
            AdminUserId = user.Id,
            LoginId = user.LoginId,
            Role = user.Role,
            IsDisabled = user.IsDisabled,
            HasTotp = user.TotpEnabled,
            InvitationPending = pending.Contains(user.Id),
            PasswordSet = !ssoOnlyIds.Contains(user.Id),
            LastLoginAt = user.LastLoginAt?.UtcDateTime,
            CreatedAt = user.CreatedAt.UtcDateTime,
        }).ToList();
    }

    /// <summary>管理者を追加し、招待を発行する。</summary>
    /// <remarks>
    /// **パスワードは決めない。** 誰も知らない値でハッシュを埋めておき、
    /// 招待を受け取るまでは**どんな入力でもログインできない**状態にする。
    /// </remarks>
    public async Task<(AdminUserOutcome Outcome, IssuedInvitation? Invitation)> InviteAsync(
        Guid actorId,
        string? loginId,
        AdminRole role,
        CancellationToken cancellationToken = default)
    {
        var normalized = loginId?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 256)
        {
            return (AdminUserOutcome.InvalidInput, null);
        }

        // **誰も知らない値。** 既定のパスワードを配らないための埋め草であって、資格情報ではない。
        // 同じログイン ID（大文字小文字は区別しない）が既にあれば、Core の窓口が null を返す
        var user = await store
            .CreateAsync(
                normalized,
                hasher.Hash(Guid.NewGuid().ToString()),
                AdminRoleMap.ToName(role),
                language: string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return (AdminUserOutcome.DuplicateLoginId, null);
        }

        logger.LogInformation(
            "管理者を追加した（AdminUserId={AdminUserId}、Role={Role}、招いたのは {ActorId}）",
            user.Id,
            role,
            actorId);

        var invitation = await IssueInvitationAsync(actorId, user.Id, cancellationToken)
            .ConfigureAwait(false);
        return (AdminUserOutcome.Succeeded, invitation);
    }

    /// <summary>招待を出し直す。**前の招待は使えなくなる。**</summary>
    /// <remarks>
    /// 招待の紙を無くしたときと、**パスワードを忘れたとき**の両方に使う。
    /// 2 要素は消さないので、これだけで乗っ取られることはない。
    /// </remarks>
    public async Task<(AdminUserOutcome Outcome, IssuedInvitation? Invitation)> ReissueInvitationAsync(
        Guid actorId,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var target = await store.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return (AdminUserOutcome.NotFound, null);
        }

        if (target.IsDisabled)
        {
            // **止めた相手を呼び戻さない。** 先に有効へ戻すこと
            return (AdminUserOutcome.InvalidInput, null);
        }

        var invitation = await IssueInvitationAsync(actorId, targetId, cancellationToken)
            .ConfigureAwait(false);
        return (AdminUserOutcome.Succeeded, invitation);
    }

    /// <summary>招待を受け取り、パスワードを自分で決める。</summary>
    /// <returns>通ったときは、その管理者。</returns>
    /// <remarks>
    /// **ここは認証を通っていない相手が叩く。** 無い・期限切れ・使用済みを区別して返さない。
    /// </remarks>
    /// <param name="language">
    /// 文言の言語。**条件に合わないときの理由を、この言語で返す。**
    /// </param>
    public async Task<(AdminUserOutcome Outcome, AdminUser? User, string? PasswordProblem)> AcceptInvitationAsync(
        string? token,
        string? password,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (AdminUserOutcome.InvitationInvalid, null, null);
        }

        // **招待を先に引く。** 条件の判定に**その人のログイン ID**が要る（Issue #157）
        var invitation = await invitations
            .FindByTokenHashAsync(InvitationToken.HashOf(token), cancellationToken).ConfigureAwait(false);

        if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt.UtcDateTime <= Now)
        {
            return (AdminUserOutcome.InvitationInvalid, null, null);
        }

        var user = await store.FindByIdAsync(invitation.AdminUserId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null || user.IsDisabled)
        {
            return (AdminUserOutcome.InvitationInvalid, null, null);
        }

        // **条件を満たさないパスワードは、招待を使い切る前に断る**（Issue #157）。
        // 使い切ってから断ると、招待が死んで受け取れなくなる
        if (policy.Check(password, user.LoginId, language) is { } passwordProblem)
        {
            return (AdminUserOutcome.WeakPassword, null, passwordProblem);
        }

        // **先に使い切る。** パスワードを入れてから印を付けると、同時に来た 2 つが両方通る
        if (!await invitations.TryConsumeAsync(invitation.InvitationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return (AdminUserOutcome.InvitationInvalid, null, null);
        }

        await store.SetPasswordHashAsync(user.Id, hasher.Hash(password!), cancellationToken)
            .ConfigureAwait(false);

        // **本人がパスワードを決めたので「設定済み」にする**（Issue #1086）。
        // SAML・Pleasanter の JIT 管理者へ招待を出し直して、パスワードを持たせた場合もここで直る
        await settings.SetPasswordSetAsync(user.Id, true, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("招待からパスワードを決めた（AdminUserId={AdminUserId}）", user.Id);
        return (AdminUserOutcome.Succeeded, user, null);
    }

    /// <summary>自分のパスワードを変える。</summary>
    /// <remarks>**今のパスワードを必ず確かめる。** 乗っ取られた画面から締め出されないため。</remarks>
    public async Task<AdminUserOutcome> ChangeOwnPasswordAsync(
        Guid actorId,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await store.FindByIdAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return AdminUserOutcome.NotFound;
        }

        if (!policy.IsAcceptable(newPassword, user.LoginId))
        {
            return AdminUserOutcome.WeakPassword;
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return AdminUserOutcome.InvalidInput;
        }

        var checkOutcome = await CheckOwnPasswordAsync(user, currentPassword, cancellationToken)
            .ConfigureAwait(false);
        if (checkOutcome != AdminUserOutcome.Succeeded)
        {
            return checkOutcome;
        }

        await store.SetPasswordHashAsync(actorId, hasher.Hash(newPassword!), cancellationToken)
            .ConfigureAwait(false);
        await settings.SetPasswordSetAsync(actorId, true, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("パスワードを変えた（AdminUserId={AdminUserId}）", actorId);
        return AdminUserOutcome.Succeeded;
    }

    /// <summary>
    /// 今のパスワードを確かめる。**2 要素を登録し直す前に通す関門。**
    /// </summary>
    /// <remarks>
    /// ログイン済みの画面から 2 要素を差し替えられると、
    /// **画面を奪われただけで乗っ取りが完成する。** パスワードをもう一度求めて止める。
    /// </remarks>
    public async Task<AdminUserOutcome> ConfirmOwnPasswordAsync(
        Guid actorId,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var user = await store.FindByIdAsync(actorId, cancellationToken).ConfigureAwait(false);
        return user is null
            ? AdminUserOutcome.NotFound
            : await CheckOwnPasswordAsync(user, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>止める・有効へ戻す。</summary>
    public async Task<AdminUserOutcome> SetDisabledAsync(
        Guid actorId,
        Guid targetId,
        bool isDisabled,
        CancellationToken cancellationToken = default)
    {
        var target = await store.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return AdminUserOutcome.NotFound;
        }

        // **自分は止めさせない。** 手が滑ったときに自分では戻せない
        if (isDisabled && targetId == actorId)
        {
            return AdminUserOutcome.SelfNotAllowed;
        }

        if (target.IsDisabled == isDisabled)
        {
            return AdminUserOutcome.Succeeded;
        }

        if (!isDisabled)
        {
            await store.SetDisabledAsync(targetId, false, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "管理者を有効へ戻した（AdminUserId={AdminUserId}、操作したのは {ActorId}）", targetId, actorId);
            return AdminUserOutcome.Succeeded;
        }

        switch (await store.SetDisabledAsync(targetId, true, cancellationToken).ConfigureAwait(false))
        {
            case AdminChangeResult.LastAdministrator:
                // **最後の 1 人だった。** 止めると誰も入れなくなる
                return AdminUserOutcome.LastAdministrator;

            case AdminChangeResult.NotFound:
                return AdminUserOutcome.NotFound;
        }

        // **止めた相手宛ての招待は取り消す。** 止めたのにパスワードを決められては困る
        await invitations.RevokeUnusedAsync(targetId, cancellationToken).ConfigureAwait(false);

        // **止めた相手の API キー（個人トークン）も失効させる**（Issue #1025）。
        // 検証側も毎回持ち主の状態を見るが、有効へ戻したときに古いトークンが生き返らないよう、印を付けておく
        await apiTokens
            .RevokeAllForOwnerAsync(targetId, Now, actorId, AdminApiTokenRevokeReasons.OwnerDisabled, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "管理者を止めた（AdminUserId={AdminUserId}、操作したのは {ActorId}）", targetId, actorId);
        return AdminUserOutcome.Succeeded;
    }

    /// <summary>
    /// 「パスワードなし（SSO のみ）」の印を付ける・外す（Issue #1086）。
    /// すでにいる JIT 管理者（SAML・Pleasanter のログインで作られた人）を、管理者が明示して直すための操作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>印はログインの成否を変えない。</b>パスワードのハッシュには触れず、セッションも失効させない。
    /// 変わるのは、パスキーの最後の 1 件を消してよいかの判定でパスワードを数えるかだけ。
    /// だから、パスワードで入っている管理者へ誤って付けても、<b>締め出さない</b>
    /// （最後のパスキーを消せなくなる、安全側へ倒れるだけ）。
    /// </para>
    /// <para>
    /// <b>自分自身へ「パスワードあり」に戻す操作は通さない。</b>本人がパスワードを知らないまま印だけを戻すと、
    /// 最後のパスキーを消して締め出される。他の管理者の役割・停止を自分へ行えないのと同じ考え方
    /// （<see cref="AdminUserOutcome.SelfNotAllowed"/>）。自分を「パスワードなし」にする向きは安全側なので通す。
    /// 他人を「パスワードあり」へ戻すのは、誤って付けた印を直す道として残す（ユーザー管理の権限を持つ管理者の明示の操作）。
    /// </para>
    /// <para>切り替えは自動では行わない（ログイン時の自動修正は、パスワードを持つ管理者を誤って直すおそれがあるため採らない）。</para>
    /// </remarks>
    public async Task<AdminUserOutcome> SetSsoOnlyAsync(
        Guid actorId,
        Guid targetId,
        bool ssoOnly,
        CancellationToken cancellationToken = default)
    {
        var target = await store.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return AdminUserOutcome.NotFound;
        }

        // **自分を「パスワードあり」へ戻す操作は通さない。** 知らない印を自分で立てて締め出される道を作らない
        if (!ssoOnly && targetId == actorId)
        {
            return AdminUserOutcome.SelfNotAllowed;
        }

        if (await settings.GetPasswordSetAsync(targetId, cancellationToken).ConfigureAwait(false) == !ssoOnly)
        {
            return AdminUserOutcome.Succeeded;
        }

        await settings.SetPasswordSetAsync(targetId, !ssoOnly, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "パスワードなし（SSO のみ）の印を切り替えた（AdminUserId={AdminUserId}、SSO のみ={SsoOnly}、操作したのは {ActorId}）",
            targetId,
            ssoOnly,
            actorId);
        return AdminUserOutcome.Succeeded;
    }

    /// <summary>役割を変える。</summary>
    public async Task<AdminUserOutcome> SetRoleAsync(
        Guid actorId,
        Guid targetId,
        AdminRole role,
        CancellationToken cancellationToken = default)
    {
        var target = await store.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return AdminUserOutcome.NotFound;
        }

        var roleName = AdminRoleMap.ToName(role);
        if (target.Role == roleName)
        {
            return AdminUserOutcome.Succeeded;
        }

        // **自分の役割は変えさせない。** 降ろした瞬間に戻す権限を失う
        if (targetId == actorId)
        {
            return AdminUserOutcome.SelfNotAllowed;
        }

        switch (await store.SetRoleAsync(targetId, roleName, cancellationToken).ConfigureAwait(false))
        {
            case AdminChangeResult.LastAdministrator:
                return AdminUserOutcome.LastAdministrator;

            case AdminChangeResult.NotFound:
                return AdminUserOutcome.NotFound;
        }

        // **役割を変えたら、相手のセッションをすべて失効させる。** 降格した相手が、
        // 古い役割のまま操作を続けられては困る（セッションは役割を持たないので、ここで切る）。再ログインで新しい役割になる
        await sessions.DeleteForUserAsync(targetId, exceptSessionId: null, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "役割を変えた（AdminUserId={AdminUserId}、Role={Role}、操作したのは {ActorId}）",
            targetId,
            role,
            actorId);
        return AdminUserOutcome.Succeeded;
    }

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    private async Task<IssuedInvitation> IssueInvitationAsync(
        Guid actorId,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        var token = InvitationToken.Create();
        var expiresAt = Now + options.InvitationLifetime;

        await invitations.ReplaceAsync(
            new AdminInvitation(
                InvitationId: Guid.NewGuid(),
                AdminUserId: targetId,
                TokenHash: InvitationToken.HashOf(token),
                ExpiresAt: new DateTimeOffset(expiresAt),
                CreatedBy: actorId),
            cancellationToken).ConfigureAwait(false);

        return new IssuedInvitation(targetId, token, expiresAt);
    }

    /// <summary>本人のパスワードを照合する。</summary>
    /// <remarks>
    /// **ログインと同じ道を通す。** 失敗の数え上げと締め出しをここだけ免れると、
    /// **総当たりの抜け道になる。**
    /// </remarks>
    private async Task<AdminUserOutcome> CheckOwnPasswordAsync(
        AdminUser user,
        string? password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return AdminUserOutcome.PasswordRejected;
        }

        var result = await authenticator
            .CheckPasswordAsync(user.LoginId, password, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            // **2 要素を求めない設定では SignedIn が返る。** ここでは「パスワードが合っていた」
            // ことだけを見たいので、3 つとも「通った」として扱う（Issue #154）
            PasswordOutcome.NeedsSecondFactor
                or PasswordOutcome.NeedsTotpEnrollment
                or PasswordOutcome.SignedIn =>
                AdminUserOutcome.Succeeded,
            PasswordOutcome.LockedOut => AdminUserOutcome.LockedOut,
            _ => AdminUserOutcome.PasswordRejected,
        };
    }
}
