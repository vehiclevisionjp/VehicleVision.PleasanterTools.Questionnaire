using System.Security.Cryptography;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>2 要素認証をどこまで求めるか。</summary>
/// <remarks>
/// **導入先で要件が違う。** 社内利用では重く、規程がある導入先では必須にしたい。
/// **設定はアプリ全体で 1 つ**（<c>QUESTIONNAIRE_ADMIN_TWOFACTOR</c>）。
/// </remarks>
public enum TwoFactorPolicy
{
    /// <summary>必須。**未登録なら、次回ログイン時に登録させてから通す。**</summary>
    Required,

    /// <summary>任意（既定）。**管理者ごとに、自分で登録するかを決められる。**</summary>
    Optional,

    /// <summary>
    /// 無効。**新しい登録を受け付けない。**
    /// </summary>
    /// <remarks>
    /// ⚠️ **すでに登録している人からは外さない。** 設定 1 つで既存の保護が消えるのは危ない。
    /// 外したい人は自分で解除する。
    /// </remarks>
    Disabled,
}

/// <summary>管理者の認証の設定。</summary>
public sealed record AdminAuthOptions
{
    /// <summary>設定の名前。**知らない値は起動時に落とす**（Program.cs）。</summary>
    public const string TwoFactorSetting = "QUESTIONNAIRE_ADMIN_TWOFACTOR";

    /// <summary>2 要素認証をどこまで求めるか。**既定は任意。**</summary>
    public TwoFactorPolicy TwoFactor { get; init; } = TwoFactorPolicy.Optional;

    /// <summary>締め出すまでの失敗回数。</summary>
    public int MaxFailedAttempts { get; init; } = 5;

    /// <summary>締め出す長さ。</summary>
    /// <remarks>
    /// **恒久的に締め出さない。** 正規の利用者を狙って締め出す嫌がらせが成立してしまう。
    /// </remarks>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>認証アプリに表示するサービス名。</summary>
    public string Issuer { get; init; } = "アンケート";

    /// <summary>招待が使える長さ。</summary>
    /// <remarks>
    /// **期限を必ず持たせる**（<c>_documents/非機能設計.md</c> 1 章）。
    /// 期限の無い招待は、後から拾われて使われる。
    /// </remarks>
    public TimeSpan InvitationLifetime { get; init; } = TimeSpan.FromHours(48);
}

/// <summary>パスワードの照合の結果。</summary>
public enum PasswordOutcome
{
    /// <summary>合っていた。**次は 2 要素へ進む。**</summary>
    NeedsSecondFactor,

    /// <summary>
    /// 合っていて、**2 要素は求めない**。そのままログインさせる。
    /// </summary>
    /// <remarks>
    /// 2 要素が未登録で、かつ設定が <see cref="TwoFactorPolicy.Required"/> でないとき。
    /// </remarks>
    SignedIn,

    /// <summary>合っていたが 2 要素がまだ登録されていない。**登録させてから通す。**</summary>
    NeedsTotpEnrollment,

    /// <summary>合っていない。**理由は呼び出し側でも区別して返さない。**</summary>
    Invalid,

    /// <summary>締め出し中。</summary>
    LockedOut,

    /// <summary>止められている利用者。</summary>
    Disabled,
}

/// <summary>パスワードの照合の結果。</summary>
public sealed record PasswordResult(PasswordOutcome Outcome, AdminUser? User = null, DateTime? LockedUntil = null);

/// <summary>2 要素の照合の結果。</summary>
public enum SecondFactorOutcome
{
    Succeeded,
    Invalid,
    LockedOut,
    Disabled,

    /// <summary>2 要素がまだ登録されていない。</summary>
    NotEnrolled,
}

/// <summary>2 要素の登録に必要な情報。**この時しか共有鍵を見せない。**</summary>
public sealed record TotpEnrollment(string SecretBase32, string OtpAuthUri);

/// <summary>管理者の認証。</summary>
/// <remarks>
/// <para>
/// **パスワードだけでは通さない**（<c>_documents/非機能設計.md</c> 2 章）。
/// 管理画面は全アンケートの回答に触れるため、
/// 2 要素を**任意ではなく必須**にする。
/// </para>
/// <para>
/// **どの段階で外れたかを外へ伝えない。** 「その利用者は居ない」と
/// 「パスワードが違う」を区別して返すと、利用者名の総当たりに使える。
/// </para>
/// <para>
/// 管理者・復旧コードの保管は Core.Data の <see cref="IAdminUserStore"/>。
/// 共有鍵の暗号化は、その中で <see cref="ISecretCipher"/>（<see cref="SecretCipherAdapter"/>）が行う。
/// </para>
/// </remarks>
public sealed class AdminAuthenticator(
    IAdminUserStore store,
    IAdminUserSettingsStore settings,
    PasswordHasher hasher,
    TotpService totp,
    AdminAuthOptions options,
    TimeProvider timeProvider,
    ILogger<AdminAuthenticator> logger)
{
    /// <summary>
    /// 利用者が居ないときにも同じだけ時間を使うための捨てハッシュ。
    /// **応答時間の差で利用者の実在を当てられないようにする。**
    /// </summary>
    private readonly string decoyHash = hasher.Hash(Guid.NewGuid().ToString());

    /// <summary>パスワードを照合する。</summary>
    public async Task<PasswordResult> CheckPasswordAsync(
        string loginId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var found = await store.FindForLoginAsync(loginId, cancellationToken).ConfigureAwait(false);

        if (found is not var (user, passwordHash))
        {
            // **見つからなくても照合する。** すぐ返すと、応答の速さで居ないことが分かる
            hasher.Verify(password, decoyHash);
            return new PasswordResult(PasswordOutcome.Invalid);
        }

        if (user.IsDisabled)
        {
            return new PasswordResult(PasswordOutcome.Disabled);
        }

        var now = timeProvider.GetUtcNow();
        if (user.IsLocked(now))
        {
            return new PasswordResult(PasswordOutcome.LockedOut, LockedUntil: user.LockedUntil!.Value.UtcDateTime);
        }

        var (verified, needsRehash) = hasher.Verify(password, passwordHash);
        if (!verified)
        {
            await CountFailureAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return new PasswordResult(PasswordOutcome.Invalid);
        }

        if (needsRehash)
        {
            // **通ったこの瞬間だけ、新しい書式で作り直せる。** 平文はここにしか無い
            await store.SetPasswordHashAsync(user.Id, hasher.Hash(password), cancellationToken)
                .ConfigureAwait(false);
        }

        // **パスワードが通っただけでは記録しない。** 2 要素まで通って初めてログインとする
        //
        // **登録済みなら、設定が無効でも 2 要素を求める。**
        // 設定 1 つで既存の保護が消えるのは危ない（TwoFactorPolicy.Disabled の但し書き）。
        // 未登録のときだけ、設定で分かれる。
        //
        // ⚠️ **ここでログインを記録しないこと。** この照合は
        // 「パスワードの再確認」（2 要素の登録し直しなど）にも使われるので、
        // 記録するとログインしていないのにログイン扱いになる。
        // **記録はセッションを張る側で行う**（<see cref="RecordSignInAsync"/>）。
        var outcome = user.TotpEnabled
            ? PasswordOutcome.NeedsSecondFactor
            : options.TwoFactor is TwoFactorPolicy.Required
                ? PasswordOutcome.NeedsTotpEnrollment
                : PasswordOutcome.SignedIn;

        return new PasswordResult(outcome, user);
    }

    /// <summary>使い捨てパスワードを照合する。</summary>
    public async Task<SecondFactorOutcome> VerifyTotpAsync(
        Guid adminUserId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var user = await store.FindByIdAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return SecondFactorOutcome.Invalid;
        }

        if (user.IsDisabled)
        {
            return SecondFactorOutcome.Disabled;
        }

        if (user.IsLocked(timeProvider.GetUtcNow()))
        {
            return SecondFactorOutcome.LockedOut;
        }

        if (!user.TotpEnabled)
        {
            return SecondFactorOutcome.NotEnrolled;
        }

        string? secret;
        try
        {
            secret = await store.GetTotpSecretAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            // **鍵を失うとここに来る。** 復旧コードで入って登録し直してもらう
            logger.LogError(
                "共有鍵を復号できない（AdminUserId={AdminUserId}）。鍵の設定を確認する", user.Id);
            return SecondFactorOutcome.Invalid;
        }

        if (secret is null)
        {
            return SecondFactorOutcome.NotEnrolled;
        }

        var (verified, timeStep) = totp.Verify(secret, code);
        if (!verified)
        {
            await CountFailureAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return SecondFactorOutcome.Invalid;
        }

        // **同じ時間枠は一度しか通さない。** 30 秒の間は同じ数字が有効なため
        if (!await store.TryUseTotpStepAsync(user.Id, timeStep, cancellationToken).ConfigureAwait(false))
        {
            await CountFailureAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return SecondFactorOutcome.Invalid;
        }

        await store.RecordSuccessAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return SecondFactorOutcome.Succeeded;
    }

    /// <summary>復旧コードを照合する。**通れば、そのコードは二度と使えない。**</summary>
    public async Task<SecondFactorOutcome> VerifyRecoveryCodeAsync(
        Guid adminUserId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var user = await store.FindByIdAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return SecondFactorOutcome.Invalid;
        }

        if (user.IsDisabled)
        {
            return SecondFactorOutcome.Disabled;
        }

        if (user.IsLocked(timeProvider.GetUtcNow()))
        {
            return SecondFactorOutcome.LockedOut;
        }

        var normalized = RecoveryCode.Normalize(code);
        if (normalized.Length == 0)
        {
            return SecondFactorOutcome.Invalid;
        }

        var candidates = await settings.ListUnusedRecoveryCodeHashesAsync(adminUserId, cancellationToken)
            .ConfigureAwait(false);

        // **どれと一致したかは総当たりで探すしかない。** 復旧コードは推測しにくい値なので
        // 回数は 10 本で頭打ちになり、かつ使う場面はまれ。
        // 保存しているのは塩つきのハッシュなので、入力から同じハッシュは作れない（Core の窓口は完全一致で使用済みにする）
        foreach (var candidate in candidates)
        {
            var (verified, _) = hasher.Verify(normalized, candidate);
            if (!verified)
            {
                continue;
            }

            if (!await store.TryUseRecoveryCodeAsync(adminUserId, candidate, cancellationToken)
                    .ConfigureAwait(false))
            {
                // 同時に使われた。**二度は通さない**
                break;
            }

            await store.RecordSuccessAsync(user.Id, cancellationToken).ConfigureAwait(false);
            logger.LogWarning(
                "復旧コードでログインした（AdminUserId={AdminUserId}）。2 要素の登録し直しを促す",
                user.Id);
            return SecondFactorOutcome.Succeeded;
        }

        await CountFailureAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return SecondFactorOutcome.Invalid;
    }

    /// <summary>2 要素の登録を始める。**共有鍵は確定させず、「登録の途中の鍵」として預ける。**</summary>
    /// <remarks>
    /// 認証アプリに読み込ませただけで有効にすると、
    /// **読み込みに失敗していた場合に本人が入れなくなる。**
    /// 一度打ってもらってから <see cref="CompleteTotpEnrollmentAsync"/> で確定する。
    /// 預けた鍵は暗号化して DB に置く（<see cref="IAdminUserStore.SetTotpPendingAsync"/>）。
    /// **画面から送り返させない**（差し替えられる）。
    /// </remarks>
    /// <exception cref="InvalidOperationException">2 要素を無効にしている場合。</exception>
    public async Task<TotpEnrollment> BeginTotpEnrollmentAsync(
        Guid adminUserId,
        string loginId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnrollmentAllowed();

        var secret = totp.GenerateSecret();
        await store.SetTotpPendingAsync(adminUserId, secret, cancellationToken).ConfigureAwait(false);
        return new TotpEnrollment(secret, TotpService.BuildUri(options.Issuer, loginId, secret));
    }

    /// <summary>登録の途中の共有鍵を返す。**無い・復号できないときは <c>null</c>**（登録のやり直しを求める）。</summary>
    public async Task<string?> GetPendingEnrollmentSecretAsync(
        Guid adminUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetTotpPendingAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>2 要素の登録を確定する。</summary>
    /// <returns>
    /// 復旧コード。**返せるのはこの時だけ**（保存するのはハッシュのみ）。
    /// 数字が合っていなければ <c>null</c>。
    /// </returns>
    public async Task<IReadOnlyList<string>?> CompleteTotpEnrollmentAsync(
        Guid adminUserId,
        string secretBase32,
        string code,
        CancellationToken cancellationToken = default)
    {
        EnsureEnrollmentAllowed();

        var (verified, timeStep) = totp.Verify(secretBase32, code);
        if (!verified)
        {
            return null;
        }

        // **共有鍵の確定・登録に使った時間枠・前の復旧コードの置き換えを、1 つの取引で行う。**
        // 登録に使った時間枠はもう使えない（そのままログインへ流用させない）
        var codes = RecoveryCode.Generate();
        await store.EnableTotpAsync(
            adminUserId,
            secretBase32,
            timeStep,
            codes.Select(value => hasher.Hash(RecoveryCode.Normalize(value))).ToList(),
            cancellationToken).ConfigureAwait(false);

        // **ここまで来たらログインが 1 回通ったのと同じ。**
        // パスワードと使い捨てパスワードの両方が揃っており、この後 `Admin.Session` になる。
        // 記録しないと、**入れているのに「一度も入っていない」ように見える**
        await store.RecordSuccessAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        return codes;
    }

    /// <summary>ログインできたことを記録する。</summary>
    /// <remarks>
    /// **2 要素を通らずに入る経路（<see cref="PasswordOutcome.SignedIn"/>）で使う。**
    /// 2 要素を通る経路では、その照合の側で記録している。
    /// 記録しないと、**入れているのに「一度も入っていない」ように見える**
    /// （最後の管理者を止めさせない判定がこれを見る）。
    /// </remarks>
    public Task RecordSignInAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        store.RecordSuccessAsync(adminUserId, cancellationToken);

    /// <summary>2 要素の登録を受け付けるかを確かめる。</summary>
    /// <remarks>
    /// **無効にしている導入先では、登録の口そのものを閉じる。**
    /// 画面から隠すだけでは、API を直接叩かれたときに通ってしまう。
    /// </remarks>
    private void EnsureEnrollmentAllowed()
    {
        if (options.TwoFactor is TwoFactorPolicy.Disabled)
        {
            throw new InvalidOperationException("2 要素認証を無効にしているため、登録できない");
        }
    }

    /// <summary>2 要素の登録を解除する。**復旧コードも消える。**</summary>
    /// <remarks>
    /// 端末を失った人の救済に使う。**誰が誰の登録を解除したかは監査ログへ残すこと**
    /// （呼び出し側の責任）。
    /// </remarks>
    public Task DisableTotpAsync(Guid adminUserId, CancellationToken cancellationToken = default) =>
        // **復旧コードだけ残っても意味が無い。** 2 要素を外したら一緒に消える（Core の窓口が同じ取引で消す）
        store.DisableTotpAsync(adminUserId, cancellationToken);

    /// <summary>最初の管理者を作る。**まだ 1 人も居ないときだけ通す。**</summary>
    /// <remarks>
    /// 既定のパスワードを仕込むより、**空の状態から利用者に作らせる**方が安全。
    /// 変え忘れた既定値が残らない。
    /// 排他は Core の窓口（<see cref="IAdminUserStore.CreateFirstAsync"/>。印の表の主キー）が持つ。
    /// </remarks>
    public async Task<AdminUser?> TryCreateFirstAdministratorAsync(
        string loginId,
        string password,
        CancellationToken cancellationToken = default)
    {
        // **居るときはハッシュを作らない**（無駄な計算をさせない）。最終的な排他は CreateFirstAsync が行う
        if (await store.CountAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
            return null;
        }

        var user = await store
            .CreateFirstAsync(loginId, hasher.Hash(password), language: string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        // **利用者が書いた文字列をそのままログへ出さない。**
        // 改行を含めれば、ログの行を偽装できる（CodeQL の cs/log-forging）
        logger.LogInformation("最初の管理者を作った（LoginId={LoginId}）", LogSafe.Text(loginId));
        return user;
    }

    private async Task CountFailureAsync(Guid adminUserId, CancellationToken cancellationToken)
    {
        // **数えるのも締め出すのも DB 側の 1 つの口で行う。** 並行した失敗を数え落とさない
        var failure = await store
            .RecordFailureAsync(adminUserId, options.MaxFailedAttempts, options.LockoutDuration, cancellationToken)
            .ConfigureAwait(false);

        if (failure.LockedUntil is { } lockedUntil)
        {
            logger.LogWarning(
                "失敗が続いたので締め出した（AdminUserId={AdminUserId}、{LockedUntil} まで）",
                adminUserId,
                lockedUntil);
        }
    }
}
