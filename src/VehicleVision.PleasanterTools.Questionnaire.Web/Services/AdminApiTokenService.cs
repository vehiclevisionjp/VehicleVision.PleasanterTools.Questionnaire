using System.Net;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>トークンを発行する前の本人確認の手段（Issue #1025）。</summary>
/// <remarks>
/// <b>どちらも「いま画面の前に居る本人」を確かめるため。</b>離席中の画面を奪われただけで、
/// 2 要素を迂回する永続的な資格情報を作らせない。
/// </remarks>
public abstract record AdminApiTokenReauthentication
{
    /// <summary>パスワードの再確認（パスワードでログインした管理者）。</summary>
    public sealed record Password(string? Value) : AdminApiTokenReauthentication
    {
        /// <summary>⚠️ <b>パスワードを文字列化に載せない。</b></summary>
        public override string ToString() => "Password { }";
    }

    /// <summary>
    /// 直前の再ログイン（SSO の管理者。パスワードを持たない）。
    /// <b>このセッションを作った時刻</b>が、設定の時間内（既定 5 分）であること。
    /// </summary>
    public sealed record Relogin(DateTimeOffset SessionCreatedAt) : AdminApiTokenReauthentication;
}

/// <summary>トークンの発行の依頼。</summary>
/// <param name="Name">利用者が付ける名前。</param>
/// <param name="Scopes">付けるスコープ。</param>
/// <param name="ExpiresInDays">期限（日）。<c>null</c> なら既定。<b>無期限は指定できない。</b></param>
/// <param name="IpAllowList">接続元の許可リスト（CIDR）。空なら制限しない。</param>
public sealed record AdminApiTokenIssueRequest(
    string? Name,
    IReadOnlyList<string>? Scopes,
    int? ExpiresInDays,
    IReadOnlyList<string>? IpAllowList);

/// <summary>発行の結果。<b>文言は入口側で決める。</b></summary>
public enum AdminApiTokenIssueOutcome
{
    Issued,

    /// <summary>機能が無効（既定）。</summary>
    FeatureDisabled,

    /// <summary>持ち主が居ない・止められている。</summary>
    OwnerUnavailable,

    /// <summary>その役割は発行できない（<c>UserAdministrator</c>・<c>Auditor</c>）。</summary>
    RoleNotAllowed,

    NameInvalid,

    /// <summary>スコープが空・許可リストに無い。</summary>
    ScopesInvalid,

    /// <summary>許可リストにはあるが、持ち主の役割が持たない（例: 編集者の <c>surveys.publish</c>）。</summary>
    ScopeExceedsRole,

    ExpiryInvalid,

    IpAllowListInvalid,

    /// <summary>いま使えるトークンが上限に達している。</summary>
    LimitReached,

    /// <summary>パスワードが違う。</summary>
    PasswordRejected,

    /// <summary>パスワードの試行が続いて締め出し中。</summary>
    LockedOut,

    /// <summary>SSO の管理者で、直前の再ログインではない。</summary>
    ReloginRequired,
}

/// <summary>発行した直前のトークン。<b>平文を返せるのはこの時だけ。</b></summary>
public sealed record IssuedAdminApiToken(AdminApiTokenRecord Record, string Token)
{
    /// <summary>⚠️ <b>平文を文字列化に載せない。</b></summary>
    public override string ToString() => $"IssuedAdminApiToken {{ TokenId = {Record.TokenId}, Prefix = {Record.Prefix} }}";
}

/// <summary>トークンを使えなかった理由。<b>外へは区別せず、すべて同じ 401 にする</b>（理由は監査へ）。</summary>
public enum AdminApiTokenRejection
{
    /// <summary>機能が無効。</summary>
    FeatureDisabled,

    /// <summary>形が違う・そんなトークンは無い。</summary>
    NotFound,

    Expired,

    Revoked,

    /// <summary>持ち主が居ない（削除）・止められている。</summary>
    OwnerUnavailable,

    /// <summary>接続元が許可リストに入っていない。</summary>
    IpNotAllowed,
}

/// <summary>トークンの検証の結果。</summary>
/// <param name="Rejection">使えなかったときの理由。使えるなら <c>null</c>。</param>
/// <param name="Token">トークン（<b>見つかったとき</b>。監査へ接頭辞を残すため）。</param>
/// <param name="Owner">持ち主（使えるときだけ）。</param>
/// <param name="EffectivePermissions">実効の権限（使えるときだけ。<b>持ち主の役割 ∩ スコープ</b>）。</param>
public sealed record AdminApiTokenValidation(
    AdminApiTokenRejection? Rejection,
    AdminApiTokenRecord? Token,
    AdminUser? Owner,
    IReadOnlySet<string> EffectivePermissions)
{
    /// <summary>使えるか。</summary>
    public bool Succeeded => Rejection is null;
}

/// <summary>
/// 管理 API の個人トークン（API キー）の発行・一覧・失効・検証（Issue #1025。親 #1012）。
/// </summary>
/// <remarks>
/// <para>
/// <b>認証の仕組み（Bearer の認証スキーム）はここに無い</b>（#1026）。ここにあるのは、
/// 「トークンの値を受け取って、使えるか・何の権限か」を決める<b>資格情報に依らない 1 か所</b>
/// （<see cref="ValidateAsync"/>）。#1026 はこれを呼ぶだけでよい。
/// </para>
/// <para>
/// <b>平文はログ・監査・例外メッセージのどこにも出さない。</b>この型が平文を持つのは
/// <see cref="IssueAsync"/> が返す一瞬だけで、保存するのはハッシュだけ。
/// </para>
/// </remarks>
public sealed class AdminApiTokenService(
    IAdminApiTokenStore tokens,
    IAdminUserStore users,
    AdminUserService userService,
    AdminApiTokenOptions options,
    TimeProvider timeProvider,
    ILogger<AdminApiTokenService> logger)
{
    /// <summary>最後に使った日時の更新を間引く間隔。</summary>
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    private const int MaxNameLength = 100;

    /// <summary>機能が有効か。</summary>
    public bool Enabled => options.Enabled;

    /// <summary>設定（画面が既定の期限などを出すのに使う）。</summary>
    public AdminApiTokenOptions Options => options;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>トークンを発行する。</summary>
    /// <remarks>
    /// 順序は、機能 → 持ち主 → 役割 → 入力 → <b>本人確認</b> → 上限 → 作成。
    /// 本人確認（パスワードの試行は失敗を数える）は、入力の誤りで試行を消費しないよう、入力の検査の後に置く。
    /// </remarks>
    public async Task<(AdminApiTokenIssueOutcome Outcome, IssuedAdminApiToken? Issued)> IssueAsync(
        Guid ownerId,
        AdminApiTokenIssueRequest request,
        AdminApiTokenReauthentication reauthentication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reauthentication);

        if (!options.Enabled)
        {
            return (AdminApiTokenIssueOutcome.FeatureDisabled, null);
        }

        var owner = await users.FindByIdAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.IsDisabled)
        {
            return (AdminApiTokenIssueOutcome.OwnerUnavailable, null);
        }

        if (!AdminApiTokenScopes.CanIssue(owner.Role))
        {
            return (AdminApiTokenIssueOutcome.RoleNotAllowed, null);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            return (AdminApiTokenIssueOutcome.NameInvalid, null);
        }

        var scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0 || scopes.Any(scope => !AdminApiTokenScopes.IsAllowed(scope)))
        {
            return (AdminApiTokenIssueOutcome.ScopesInvalid, null);
        }

        // **自分の権限を超えるスコープは付けられない**（編集者は公開を付けられない）
        var issuable = AdminApiTokenScopes.IssuableFor(owner.Role);
        if (scopes.Any(scope => !issuable.Contains(scope)))
        {
            return (AdminApiTokenIssueOutcome.ScopeExceedsRole, null);
        }

        // **期限は必須。** 指定しなければ既定。0 以下・上限超えは断る。無期限は作れない
        var days = request.ExpiresInDays ?? options.DefaultDays;
        if (days < 1 || days > options.MaxDays)
        {
            return (AdminApiTokenIssueOutcome.ExpiryInvalid, null);
        }

        var ipAllowList = AdminApiTokenScopes.NormalizeIpAllowList(request.IpAllowList);
        if (ipAllowList is null)
        {
            return (AdminApiTokenIssueOutcome.IpAllowListInvalid, null);
        }

        var reauth = await ReauthenticateAsync(ownerId, reauthentication, cancellationToken).ConfigureAwait(false);
        if (reauth != AdminApiTokenIssueOutcome.Issued)
        {
            return (reauth, null);
        }

        var now = Now;
        if (await tokens.CountActiveAsync(ownerId, now, cancellationToken).ConfigureAwait(false)
            >= AdminApiTokenOptions.MaxActivePerOwner)
        {
            return (AdminApiTokenIssueOutcome.LimitReached, null);
        }

        var secret = AdminApiTokenSecret.Create();
        var tokenId = Guid.NewGuid();
        var expiresAt = now.AddDays(days);

        await tokens.CreateAsync(
            new NewAdminApiToken(
                tokenId,
                ownerId,
                name,
                secret.Prefix,
                secret.Hash,
                scopes,
                ipAllowList,
                now,
                expiresAt),
            cancellationToken).ConfigureAwait(false);

        // ⚠️ **ログへ出すのは識別子と接頭辞だけ。** 平文・ハッシュは出さない
        logger.LogInformation(
            "管理 API のトークンを発行した（TokenId={TokenId}、Prefix={Prefix}、AdminUserId={AdminUserId}）",
            tokenId,
            secret.Prefix,
            ownerId);

        var record = new AdminApiTokenRecord(
            tokenId, ownerId, name, secret.Prefix, scopes, ipAllowList, now, expiresAt, null, null, null, null, null);
        return (AdminApiTokenIssueOutcome.Issued, new IssuedAdminApiToken(record, secret.Plain));
    }

    /// <summary>持ち主のトークンの一覧（新しい順。失効・期限切れを含む）。<b>値は含まない。</b></summary>
    public Task<IReadOnlyList<AdminApiTokenRecord>> ListAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default) =>
        tokens.ListByOwnerAsync(ownerId, cancellationToken);

    /// <summary>1 本失効させる。<b>その場で効く</b>（要求のたびに DB を引くため）。</summary>
    /// <param name="ownerId">
    /// 持ち主の組で絞る（他人の識別子を渡されても失効しない）。
    /// </param>
    /// <param name="revokedBy">失効させた管理者。</param>
    /// <param name="reason"><see cref="AdminApiTokenRevokeReasons"/>。</param>
    /// <returns>この呼び出しで失効させたトークン。無い・すでに失効していれば <c>null</c>。</returns>
    public async Task<AdminApiTokenRecord?> RevokeAsync(
        Guid tokenId,
        Guid ownerId,
        Guid revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var existing = await tokens.FindAsync(tokenId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.OwnerAdminUserId != ownerId)
        {
            return null;
        }

        if (!await tokens.RevokeAsync(tokenId, ownerId, Now, revokedBy, reason, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        logger.LogInformation(
            "管理 API のトークンを失効させた（TokenId={TokenId}、Prefix={Prefix}、AdminUserId={AdminUserId}、Reason={Reason}）",
            tokenId,
            existing.Prefix,
            ownerId,
            reason);
        return existing;
    }

    /// <summary>持ち主の、いま使えるトークンをすべて失効させる。</summary>
    /// <remarks>
    /// 持ち主の停止・パスワードの変更・2 要素の解除、ほかの管理者による一括失効で使う。
    /// </remarks>
    /// <returns>失効させたトークン（接頭辞つき。監査に残すため）。</returns>
    public async Task<IReadOnlyList<AdminApiTokenRecord>> RevokeAllAsync(
        Guid ownerId,
        Guid? revokedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var revoked = await tokens.RevokeAllForOwnerAsync(ownerId, Now, revokedBy, reason, cancellationToken)
            .ConfigureAwait(false);

        if (revoked.Count > 0)
        {
            logger.LogInformation(
                "管理 API のトークンをまとめて失効させた（AdminUserId={AdminUserId}、件数={Count}、Reason={Reason}）",
                ownerId,
                revoked.Count,
                reason);
        }

        return revoked;
    }

    /// <summary>
    /// トークンの値を検証する。<b>使えるか・持ち主・実効の権限</b>を返す。#1026 の Bearer 認証がここを呼ぶ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>要求のたびに持ち主の状態を見る</b>（停止・削除・役割の降格がその場で効く）。
    /// 実効の権限は<b>持ち主の「いま」の役割 ∩ スコープ</b>で、トークンへ焼かない。
    /// </para>
    /// <para>
    /// 呼ぶ側は、<see cref="AdminApiTokenRejection"/> を外へ区別して返さないこと
    /// （不存在・期限切れ・失効・持ち主の停止は同じ <c>401</c>。理由は監査へ）。
    /// </para>
    /// </remarks>
    /// <param name="token">Authorization ヘッダの値から取り出したトークン。<b>ログへ出さない。</b></param>
    /// <param name="remoteAddress">接続元（許可リストの照合に使う）。</param>
    public async Task<AdminApiTokenValidation> ValidateAsync(
        string? token,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        static AdminApiTokenValidation Reject(AdminApiTokenRejection rejection, AdminApiTokenRecord? found = null) =>
            new(rejection, found, null, new HashSet<string>());

        if (!options.Enabled)
        {
            return Reject(AdminApiTokenRejection.FeatureDisabled);
        }

        if (!AdminApiTokenSecret.LooksLikeToken(token))
        {
            return Reject(AdminApiTokenRejection.NotFound);
        }

        var record = await tokens.FindByHashAsync(AdminApiTokenSecret.HashOf(token!), cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Reject(AdminApiTokenRejection.NotFound);
        }

        var now = Now;
        if (record.RevokedAt is not null)
        {
            return Reject(AdminApiTokenRejection.Revoked, record);
        }

        if (record.ExpiresAt <= now)
        {
            return Reject(AdminApiTokenRejection.Expired, record);
        }

        var owner = await users.FindByIdAsync(record.OwnerAdminUserId, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.IsDisabled)
        {
            return Reject(AdminApiTokenRejection.OwnerUnavailable, record);
        }

        if (!AdminApiTokenScopes.IsIpAllowed(record.IpAllowList, remoteAddress))
        {
            return Reject(AdminApiTokenRejection.IpNotAllowed, record);
        }

        // **最後に使った日時は間引いて書く。** 書けなくても認証は通す（記録の失敗で止めない）
        try
        {
            await tokens.TouchLastUsedAsync(
                record.TokenId, now, remoteAddress?.ToString(), LastUsedInterval, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "管理 API のトークンの最終使用日時を更新できなかった（TokenId={TokenId}）", record.TokenId);
        }

        return new AdminApiTokenValidation(
            null,
            record,
            owner,
            AdminApiTokenScopes.EffectivePermissions(owner.Role, record.Scopes));
    }

    private async Task<AdminApiTokenIssueOutcome> ReauthenticateAsync(
        Guid ownerId,
        AdminApiTokenReauthentication reauthentication,
        CancellationToken cancellationToken)
    {
        switch (reauthentication)
        {
            case AdminApiTokenReauthentication.Relogin relogin:
                // **セッションを作った時刻が、設定の時間内か。** 未来の時刻は信用しない
                var age = timeProvider.GetUtcNow() - relogin.SessionCreatedAt;
                return age >= TimeSpan.Zero && age <= options.ReloginWindow
                    ? AdminApiTokenIssueOutcome.Issued
                    : AdminApiTokenIssueOutcome.ReloginRequired;

            case AdminApiTokenReauthentication.Password password:
                // **ログインと同じ道でパスワードを確かめる。** 失敗は数えて締め出す（ここだけ免れると総当たりの抜け道）
                return await userService.ConfirmOwnPasswordAsync(ownerId, password.Value, cancellationToken)
                    .ConfigureAwait(false) switch
                {
                    AdminUserOutcome.Succeeded => AdminApiTokenIssueOutcome.Issued,
                    AdminUserOutcome.LockedOut => AdminApiTokenIssueOutcome.LockedOut,
                    _ => AdminApiTokenIssueOutcome.PasswordRejected,
                };

            default:
                return AdminApiTokenIssueOutcome.PasswordRejected;
        }
    }
}
