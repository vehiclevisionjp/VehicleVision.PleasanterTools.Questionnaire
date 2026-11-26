using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using VehicleVision.PleasanterTools.Questionnaire.Data;
using VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

// Core.Web のパスキーの流れが使う受け口を、Core.Data のストアと本アプリの認証の実装で満たすアダプター（Issue #1082）。
// Core.Web と Core.Data は互いに参照しないため、橋渡しはここ（アプリ）が行う。管理者・セッション・監査の表は
// Core の共有表（Core_AdminUsers ほか）なので、独自の表との対応づけは要らない。

/// <summary>パスキー（資格情報）の保管先。Core.Data の <see cref="IAdminPasskeyStore"/> を包む。</summary>
public sealed class PasskeyCredentialStoreAdapter(IAdminPasskeyStore store) : IPasskeyCredentialStore
{
    public async Task<IReadOnlyList<PasskeyCredential>> ListForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        [.. (await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false)).Select(Map)];

    public Task<int> CountForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        store.CountForUserAsync(userId, cancellationToken);

    public async Task<PasskeyCredential?> FindByCredentialIdAsync(
        byte[] credentialId, CancellationToken cancellationToken = default) =>
        await store.FindByCredentialIdAsync(credentialId, cancellationToken).ConfigureAwait(false) is { } found
            ? Map(found)
            : null;

    public Task<Guid?> AddAsync(NewPasskeyCredential passkey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        return store.AddAsync(
            new NewAdminPasskey(
                passkey.UserId,
                passkey.CredentialId,
                passkey.PublicKey,
                passkey.SignCount,
                passkey.AaGuid,
                passkey.Transports,
                passkey.BackupEligible,
                passkey.BackedUp,
                passkey.Name),
            cancellationToken);
    }

    public Task<bool> RenameAsync(
        Guid userId, Guid id, string name, CancellationToken cancellationToken = default) =>
        store.RenameAsync(userId, id, name, cancellationToken);

    public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(userId, id, cancellationToken);

    public Task<int> DeleteForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        store.DeleteForUserAsync(userId, cancellationToken);

    public Task<bool> RecordSignInAsync(
        Guid id,
        long previousSignCount,
        long newSignCount,
        bool backedUp,
        CancellationToken cancellationToken = default) =>
        store.RecordSignInAsync(id, previousSignCount, newSignCount, backedUp, cancellationToken);

    private static PasskeyCredential Map(AdminPasskey passkey) =>
        new(
            passkey.Id,
            passkey.UserId,
            passkey.CredentialId,
            passkey.PublicKey,
            passkey.SignCount,
            passkey.AaGuid,
            passkey.Transports,
            passkey.BackupEligible,
            passkey.BackedUp,
            passkey.Name,
            passkey.CreatedAt,
            passkey.LastUsedAt);
}

/// <summary>パスキーのチャレンジの保管先。Core.Data の <see cref="IAdminPasskeyChallengeStore"/> を包む。</summary>
public sealed class PasskeyChallengeStoreAdapter(IAdminPasskeyChallengeStore store) : IPasskeyChallengeStore
{
    public Task StoreAsync(
        byte[] challenge,
        string purpose,
        Guid? userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        store.StoreAsync(challenge, purpose, userId, lifetime, cancellationToken);

    public Task<bool> ConsumeAsync(
        byte[] challenge,
        string purpose,
        Guid? userId,
        CancellationToken cancellationToken = default) =>
        store.ConsumeAsync(challenge, purpose, userId, cancellationToken);
}

/// <summary>管理者まわりの受け口。管理者・セッションは Core.Data の共有ストア、パスワードの照合は本アプリの認証を使う。</summary>
public sealed class PasskeyAccountsAdapter(
    IAdminUserStore users,
    IAdminUserSettingsStore userSettings,
    IAdminSessionStore sessions,
    AdminAuthenticator authenticator,
    IAdminInvitationStore invitations,
    IAdminSignInSettingsProvider signInSettings,
    ISamlOptionsProvider samlProvider,
    IPleasanterSsoOptionsProvider pleasanterSsoProvider,
    TimeProvider time) : IPasskeyAccounts
{
    public async Task<PasskeyAccount?> FindAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) is { } user
            ? new PasskeyAccount(user.Id, user.LoginId, user.IsDisabled, user.IsLocked(time.GetUtcNow()))
            : null;

    /// <summary>
    /// パスワードを再確認する。<b>ログインと同じ道（<see cref="AdminAuthenticator.CheckPasswordAsync"/>）を通す。</b>
    /// 失敗の数え上げと締め出しをここだけ免れると、総当たりの抜け道になる。
    /// </summary>
    public async Task<PasswordConfirmation> ConfirmPasswordAsync(
        Guid userId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password)
            || await users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return PasswordConfirmation.Rejected;
        }

        var result = await authenticator
            .CheckPasswordAsync(user.LoginId, password, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            // **2 要素の要否は、ここでは見ない。** パスワードが合っていたことだけを見る（再認証であって、ログインではない）
            case PasswordOutcome.NeedsSecondFactor:
            case PasswordOutcome.NeedsTotpEnrollment:
            case PasswordOutcome.SignedIn:
                // 成功したら失敗の回数を戻す（Core の約束）
                await users.RecordSuccessAsync(userId, cancellationToken).ConfigureAwait(false);
                return PasswordConfirmation.Confirmed;

            case PasswordOutcome.LockedOut:
                return PasswordConfirmation.Locked;

            default:
                return PasswordConfirmation.Rejected;
        }
    }

    public Task RecordSignInSucceededAsync(Guid userId, CancellationToken cancellationToken = default) =>
        users.RecordSuccessAsync(userId, cancellationToken);

    /// <summary>その人の全セッションを失効させる（段階を問わない。2 要素の途中状態も含む）。</summary>
    public Task RevokeSessionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        sessions.DeleteForUserAsync(userId, null, cancellationToken);

    /// <summary>
    /// 使えるログイン方法（Core の P5。<b>ほかのパスキーは数えない</b>）。
    /// パスワード（合言葉のログインが塞がれておらず、招待を受け取り済みで、<b>パスワードを持つ印がある</b>）・SAML・Pleasanter のログインの、いずれか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 合言葉のログインは、SAML も Pleasanter のログインも無効な間は塞げない
    /// （<see cref="AdminPasswordSignInPolicy"/>）ので、その間は使えるとする。救済トークンでの一時的な入口は数えない。
    /// </para>
    /// <para>
    /// <b>「パスワード設定済み」の印を見る</b>（Issue #1086。<see cref="IAdminUserSettingsStore.GetPasswordSetAsync"/>）。
    /// SAML・Pleasanter のログインで作られた管理者（JIT）は本人の知らない値のハッシュを持つので、
    /// パスワードを「使えるログイン方法」へ数えると、最後のパスキーを消した人が誰にも入れなくなる。
    /// 印が「未設定」の管理者は、パスワードを数えない。<b>SAML・Pleasanter が有効なら、それは今までどおり数える。</b>
    /// </para>
    /// <para>
    /// <b>この判定はログインの成否を変えない。</b>パスワードで入れるかは、ハッシュの照合だけが決める
    /// （印が未設定でも、パスワードのログインが通る・通らないは今までと同じ）。
    /// </para>
    /// </remarks>
    public async Task<PasskeySignInMethods> GetSignInMethodsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var saml = (await samlProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options.Enabled;
        var pleasanterSso = (await pleasanterSsoProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options.Enabled;
        var settings = await signInSettings.GetAsync(cancellationToken).ConfigureAwait(false);
        var passwordSignInOpen = settings.PasswordSignInEnabled || !(saml || pleasanterSso);

        var password = false;
        if (passwordSignInOpen
            && await userSettings.GetPasswordSetAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            var pending = await invitations.ListPendingAdminUserIdsAsync(cancellationToken).ConfigureAwait(false);
            password = !pending.Contains(userId);
        }

        return new PasskeySignInMethods(password, saml, pleasanterSso);
    }
}

/// <summary>
/// パスキーの監査イベントを、本アプリの監査ログ（Core.Data の <c>Core_AuditLogs</c>）へ書く。
/// </summary>
/// <remarks>
/// <para>
/// 経路には、管理 API の入口の集まりへ掛ける監査フィルタ（<see cref="AuditLogFilter"/>）を<b>掛けない</b>。
/// 5 つのイベントは Core のサービスが<b>明示して</b>書くので、掛けると二重に残る。
/// </para>
/// <para>
/// <b>書けなくても操作は通す。</b>操作はもう済んでおり、例外にすると「通ったのに失敗と伝える」ことになる。
/// ただし握り潰さず、ログへ出す（記録できていないこと自体が異常）。
/// </para>
/// </remarks>
public sealed class PasskeyAuditSink(
    IAuditLogStore store,
    TimeProvider time,
    ILogger<PasskeyAuditSink> logger) : IPasskeyAuditSink
{
    /// <summary>対象の種類（常に管理者）。</summary>
    public const string TargetType = "AdminUser";

    public async Task WriteAsync(PasskeyAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await store.WriteAsync(
                new AuditEntry(
                    // **目盛りは管理操作の記録と同じ（サーバのローカル時刻の数字）**。`AuditLogTime` を参照
                    OccurredAt: AuditLogTime.ToStored(TimeZoneInfo.ConvertTime(entry.OccurredAt, time.LocalTimeZone).DateTime),
                    Action: entry.Action,
                    Succeeded: entry.Succeeded,
                    StatusCode: entry.StatusCode,
                    ActorKey: entry.ActorKey,
                    ActorLoginId: entry.ActorLoginId,
                    TargetType: TargetType,
                    TargetId: entry.TargetId,
                    DetailJson: entry.DetailJson,
                    IpAddress: entry.IpAddress),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "パスキーの監査イベントを監査ログへ残せなかった（{Action}）",
                LogSafe.Text(entry.Action));
        }
    }
}

/// <summary>パスキーの口が、本アプリのセッション・権限とつなぐための受け口。</summary>
public sealed class AdminPasskeyHost : IAdminPasskeyHost
{
    /// <summary>
    /// ログイン中（<see cref="AdminAuthSchemes.Session"/> の段階だけ。途中状態では <c>null</c>）の本人。
    /// <b>個人トークン（Bearer）は本人として扱わない</b>（パスキーは人の操作。スキームを明示して cookie だけを見る）。
    /// 権限は、復元した principal の役割から<b>その都度</b>引く（cookie へ焼かない）。
    /// </summary>
    public async ValueTask<AdminPasskeyCaller?> GetCallerAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await context.AuthenticateAsync(AdminAuthSchemes.Session).ConfigureAwait(false);
        if (!result.Succeeded
            || result.Principal is not { } principal
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || principal.FindFirstValue(ClaimTypes.Name) is not { Length: > 0 } loginId)
        {
            return null;
        }

        var permissions = AdminPermissions.Of(principal.FindFirstValue(ClaimTypes.Role))
            .ToHashSet(StringComparer.Ordinal);
        return new AdminPasskeyCaller(userId, loginId, permissions);
    }

    /// <summary>
    /// パスキーのログインに成功した人のセッションを張る。<b>途中状態（Pending）を消してから Session を張る。</b>
    /// パスキーで入ったセッションは 2 要素を満たす扱いで、途中状態を経ない。
    /// </summary>
    public Task SignInAsync(HttpContext context, PasskeySignedIn user, string via)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);
        return AdminAuthEndpoints.SignInSessionAsync(context, user.UserId, sso: null, via);
    }
}
