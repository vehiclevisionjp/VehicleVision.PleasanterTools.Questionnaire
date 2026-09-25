using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Pleasanter から来た利用者をどう扱ったか。</summary>
/// <remarks>SAML の <see cref="SamlSignInOutcome"/> と同じ分け方。</remarks>
public enum PleasanterSsoSignInOutcome
{
    /// <summary>そのまま通す。</summary>
    SignedIn,

    /// <summary>本アプリ側で 2 要素を登録している。**先に 2 要素を通す。**</summary>
    NeedsSecondFactor,

    /// <summary>2 要素が未登録で、かつ設定が <see cref="TwoFactorPolicy.Required"/>。**登録させてから通す。**</summary>
    NeedsTotpEnrollment,

    /// <summary>本アプリに居ないので通さない（<see cref="PleasanterSsoUnknownUserPolicy.Reject"/>）。</summary>
    Unknown,

    /// <summary>止められている利用者。</summary>
    Disabled,

    /// <summary>許可する所属を設定していないため自動登録しない。</summary>
    NotAllowed,
}

/// <summary>Pleasanter から来た利用者の扱いの結果。</summary>
/// <param name="Registered">この場で作ったか（JIT）。**記録に残すために持つ。**</param>
public sealed record PleasanterSsoSignInResult(
    PleasanterSsoSignInOutcome Outcome,
    AdminUser? User = null,
    bool Registered = false);

/// <summary>Pleasanter が本人だと答えた利用者を、本アプリの管理者へ結び付ける（Issue #464）。</summary>
/// <remarks>
/// <para>
/// **突き合わせはログイン ID で行う**（SAML と同じ。DB へ列を足していない）。
/// Pleasanter の <c>LoginId</c> を本アプリのログイン ID として扱う。
/// </para>
/// <para>
/// ⚠️ **本アプリ側で 2 要素を登録している人は、Pleasanter から来ても 2 要素を通す。**
/// ⚠️ **2 要素を必須にしているときは、Pleasanter から来た人にも登録させる。**
/// どちらも SAML（<see cref="SamlAuthenticator"/>）と同じ判断。
/// Pleasanter 側の 2 要素（TOTP・メールのワンタイムパスワード）は、
/// **終えるまで Pleasanter が認証 cookie を出さない**ので、ここへ来る時点で済んでいる。
/// </para>
/// </remarks>
public sealed class PleasanterSsoAuthenticator(
    IAdminUserStore store,
    PasswordHasher hasher,
    AdminAuthOptions authOptions,
    ILogger<PleasanterSsoAuthenticator> logger)
{
    /// <summary>ログイン ID から管理者を決める。居なければ設定に従って作るか断る。</summary>
    public async Task<PleasanterSsoSignInResult> SignInAsync(
        string loginId,
        PleasanterSsoOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);
        ArgumentNullException.ThrowIfNull(options);

        var trimmed = loginId.Trim();
        var user = await store.FindByLoginIdAsync(trimmed, cancellationToken).ConfigureAwait(false);
        var registered = false;

        if (user is null)
        {
            if (options.UnknownUser != PleasanterSsoUnknownUserPolicy.Register)
            {
                logger.LogInformation(
                    "Pleasanter から来た利用者 {LoginId} は本アプリに居ないため通しませんでした。",
                    LogSafe.Text(trimmed));
                return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.Unknown);
            }

            if (!options.HasMembershipRestriction)
            {
                logger.LogWarning("Pleasanter SSO の許可する所属が未設定のため自動登録を拒否しました。");
                return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.NotAllowed);
            }

            user = await RegisterAsync(trimmed, options.RegisterRole, cancellationToken)
                .ConfigureAwait(false);
            registered = true;

            logger.LogInformation(
                "Pleasanter から来た利用者 {LoginId} を {Role} として作りました。",
                LogSafe.Text(trimmed),
                options.RegisterRole);
        }

        if (user.IsDisabled)
        {
            return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.Disabled, user);
        }

        if (user.HasTotp)
        {
            // **登録済みの 2 要素は、Pleasanter 経由でも省かせない。**
            // ⚠️ 方針が Disabled でも省かない（設定 1 つで保護が消えるのは危ない）
            return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.NeedsSecondFactor, user, registered);
        }

        if (authOptions.TwoFactor is TwoFactorPolicy.Required)
        {
            return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.NeedsTotpEnrollment, user, registered);
        }

        await store.RecordSuccessAsync(user.AdminUserId, cancellationToken).ConfigureAwait(false);
        return new PleasanterSsoSignInResult(PleasanterSsoSignInOutcome.SignedIn, user, registered);
    }

    /// <summary>その場で管理者を作る（JIT）。**パスワードは持たせない**（SAML と同じ）。</summary>
    private async Task<AdminUser> RegisterAsync(
        string loginId,
        AdminRole registerRole,
        CancellationToken cancellationToken)
    {
        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = loginId,

            // **推測できない値。** 誰にも渡さないので、パスワードでは入れない
            PasswordHash = hasher.Hash(Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                + Convert.ToBase64String(Guid.NewGuid().ToByteArray())),
            Role = registerRole,
        };

        await store.CreateAsync(user, cancellationToken).ConfigureAwait(false);

        return await store.FindByLoginIdAsync(loginId, cancellationToken).ConfigureAwait(false) ?? user;
    }
}
