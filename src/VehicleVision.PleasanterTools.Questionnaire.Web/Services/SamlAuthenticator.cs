using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>IdP から来た利用者をどう扱ったか。</summary>
public enum SamlSignInOutcome
{
    /// <summary>そのまま通す。</summary>
    SignedIn,

    /// <summary>本アプリ側で 2 要素を登録している。**先に 2 要素を通す。**</summary>
    NeedsSecondFactor,

    /// <summary>本アプリに居ないので通さない（<see cref="SamlUnknownUserPolicy.Reject"/>）。</summary>
    Unknown,

    /// <summary>止められている利用者。</summary>
    Disabled,
}

/// <summary>IdP から来た利用者の扱いの結果。</summary>
/// <param name="Registered">この場で作ったか（JIT）。**記録に残すために持つ。**</param>
public sealed record SamlSignInResult(
    SamlSignInOutcome Outcome,
    AdminUser? User = null,
    bool Registered = false);

/// <summary>IdP が本人だと言ってきた利用者を、本アプリの管理者へ結び付ける（Issue #166）。</summary>
/// <remarks>
/// <para>
/// **署名の検証はここではしない。** ここへ来るのは、署名・発行者・宛先・有効期間まで
/// 通った後の値だけ（<c>AdminSamlEndpoints</c>）。
/// </para>
/// <para>
/// **突き合わせはログイン ID で行う。** IdP の <c>NameID</c>（または属性）を
/// 本アプリのログイン ID として扱う。DB へ列を足していないので、
/// **IdP 側でメールアドレスを変えると別人になる。** 手順書に注意を書いてある。
/// </para>
/// <para>
/// ⚠️ **本アプリ側で 2 要素を登録している人は、SAML で来ても 2 要素を通す。**
/// IdP の多要素に任せきりにすると、「2 要素を登録済み」の相手が
/// IdP 経由なら 1 要素で入れることになり、**設定した保護が弱くなる。**
/// </para>
/// </remarks>
public sealed class SamlAuthenticator(
    IAdminUserStore store,
    PasswordHasher hasher,
    SamlOptions options,
    ILogger<SamlAuthenticator> logger)
{
    /// <summary>ログイン ID から管理者を決める。居なければ設定に従って作るか断る。</summary>
    public async Task<SamlSignInResult> SignInAsync(
        string loginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginId);

        var trimmed = loginId.Trim();
        var user = await store.FindByLoginIdAsync(trimmed, cancellationToken).ConfigureAwait(false);
        var registered = false;

        if (user is null)
        {
            if (options.UnknownUser != SamlUnknownUserPolicy.Register)
            {
                // **居ない相手を黙って通さない。** 記録には残す
                logger.LogInformation(
                    "SAML で来た利用者 {LoginId} は本アプリに居ないため通しませんでした。",
                    LogSafe.Text(trimmed));
                return new SamlSignInResult(SamlSignInOutcome.Unknown);
            }

            user = await RegisterAsync(trimmed, cancellationToken).ConfigureAwait(false);
            registered = true;

            logger.LogInformation(
                "SAML で来た利用者 {LoginId} を {Role} として作りました。",
                LogSafe.Text(trimmed),
                options.RegisterRole);
        }

        if (user.IsDisabled)
        {
            return new SamlSignInResult(SamlSignInOutcome.Disabled, user);
        }

        if (user.HasTotp)
        {
            // **登録済みの 2 要素は、IdP 経由でも省かせない**
            return new SamlSignInResult(SamlSignInOutcome.NeedsSecondFactor, user, registered);
        }

        await store.RecordSuccessAsync(user.AdminUserId, cancellationToken).ConfigureAwait(false);
        return new SamlSignInResult(SamlSignInOutcome.SignedIn, user, registered);
    }

    /// <summary>その場で管理者を作る（JIT）。</summary>
    /// <remarks>
    /// **パスワードは持たせない。** 誰も知らない値を入れておくので、
    /// この利用者はパスワードのログインでは通らない。
    /// 使えるようにしたいときは、招待の手順で本人にパスワードを決めさせる。
    /// </remarks>
    private async Task<AdminUser> RegisterAsync(string loginId, CancellationToken cancellationToken)
    {
        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = loginId,

            // **推測できない値。** 誰にも渡さないので、パスワードでは入れない
            PasswordHash = hasher.Hash(Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                + Convert.ToBase64String(Guid.NewGuid().ToByteArray())),
            Role = options.RegisterRole,
        };

        await store.CreateAsync(user, cancellationToken).ConfigureAwait(false);

        // **作った直後の姿を DB から読み直す。** 既定値（作成日時など）を持った形で返す
        return await store.FindByLoginIdAsync(loginId, cancellationToken).ConfigureAwait(false) ?? user;
    }
}
