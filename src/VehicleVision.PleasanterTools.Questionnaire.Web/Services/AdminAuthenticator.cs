using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者の認証の設定。</summary>
public sealed record AdminAuthOptions
{
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

/// <summary>合言葉の照合の結果。</summary>
public enum PasswordOutcome
{
    /// <summary>合っていた。**次は 2 要素へ進む。**</summary>
    NeedsSecondFactor,

    /// <summary>合っていたが 2 要素がまだ登録されていない。**登録させてから通す。**</summary>
    NeedsTotpEnrollment,

    /// <summary>合っていない。**理由は呼び出し側でも区別して返さない。**</summary>
    Invalid,

    /// <summary>締め出し中。</summary>
    LockedOut,

    /// <summary>止められている利用者。</summary>
    Disabled,
}

/// <summary>合言葉の照合の結果。</summary>
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
/// **合言葉だけでは通さない**（<c>_documents/非機能設計.md</c> 2 章）。
/// 管理画面は全アンケートの回答に触れるため、
/// 2 要素を**任意ではなく必須**にする。
/// </para>
/// <para>
/// **どの段階で外れたかを外へ伝えない。** 「その利用者は居ない」と
/// 「合言葉が違う」を区別して返すと、利用者名の総当たりに使える。
/// </para>
/// </remarks>
public sealed class AdminAuthenticator(
    IAdminUserStore store,
    PasswordHasher hasher,
    TotpService totp,
    SecretProtector protector,
    AdminAuthOptions options,
    TimeProvider timeProvider,
    ILogger<AdminAuthenticator> logger)
{
    /// <summary>
    /// 利用者が居ないときにも同じだけ時間を使うための捨てハッシュ。
    /// **応答時間の差で利用者の実在を当てられないようにする。**
    /// </summary>
    private readonly string decoyHash = hasher.Hash(Guid.NewGuid().ToString());

    /// <summary>合言葉を照合する。</summary>
    public async Task<PasswordResult> CheckPasswordAsync(
        string loginId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await store.FindByLoginIdAsync(loginId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            // **見つからなくても照合する。** すぐ返すと、応答の速さで居ないことが分かる
            hasher.Verify(password, decoyHash);
            return new PasswordResult(PasswordOutcome.Invalid);
        }

        if (user.IsDisabled)
        {
            return new PasswordResult(PasswordOutcome.Disabled);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return new PasswordResult(PasswordOutcome.LockedOut, LockedUntil: lockedUntil);
        }

        var (verified, needsRehash) = hasher.Verify(password, user.PasswordHash);
        if (!verified)
        {
            await CountFailureAsync(user, cancellationToken).ConfigureAwait(false);
            return new PasswordResult(PasswordOutcome.Invalid);
        }

        if (needsRehash)
        {
            // **通ったこの瞬間だけ、新しい書式で作り直せる。** 平文はここにしか無い
            await store.UpdatePasswordHashAsync(user.AdminUserId, hasher.Hash(password), cancellationToken)
                .ConfigureAwait(false);
        }

        // **合言葉が通っただけでは記録しない。** 2 要素まで通って初めてログインとする
        return new PasswordResult(
            user.HasTotp ? PasswordOutcome.NeedsSecondFactor : PasswordOutcome.NeedsTotpEnrollment,
            user);
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return SecondFactorOutcome.LockedOut;
        }

        if (!user.HasTotp || user.TotpSecretEncrypted is null)
        {
            return SecondFactorOutcome.NotEnrolled;
        }

        var secret = protector.Unprotect(user.TotpSecretEncrypted);
        if (secret is null)
        {
            // **鍵を失うとここに来る。** 復旧コードで入って登録し直してもらう
            logger.LogError(
                "共有鍵を復号できない（AdminUserId={AdminUserId}）。鍵の設定を確認する", user.AdminUserId);
            return SecondFactorOutcome.Invalid;
        }

        var (verified, timeStep) = totp.Verify(secret, code);
        if (!verified)
        {
            await CountFailureAsync(user, cancellationToken).ConfigureAwait(false);
            return SecondFactorOutcome.Invalid;
        }

        // **同じ時間枠は一度しか通さない。** 30 秒の間は同じ数字が有効なため
        if (!await store.TryConsumeTotpTimeStepAsync(user.AdminUserId, timeStep, cancellationToken)
                .ConfigureAwait(false))
        {
            await CountFailureAsync(user, cancellationToken).ConfigureAwait(false);
            return SecondFactorOutcome.Invalid;
        }

        await store.RecordSuccessAsync(user.AdminUserId, cancellationToken).ConfigureAwait(false);
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            return SecondFactorOutcome.LockedOut;
        }

        var normalized = RecoveryCode.Normalize(code);
        if (normalized.Length == 0)
        {
            return SecondFactorOutcome.Invalid;
        }

        var candidates = await store.ListUnusedRecoveryCodesAsync(adminUserId, cancellationToken)
            .ConfigureAwait(false);

        // **どれと一致したかは総当たりで探すしかない。** 復旧コードは推測しにくい値なので
        // 回数は 10 本で頭打ちになり、かつ使う場面はまれ
        foreach (var candidate in candidates)
        {
            var (verified, _) = hasher.Verify(normalized, candidate.CodeHash);
            if (!verified)
            {
                continue;
            }

            if (!await store.TryConsumeRecoveryCodeAsync(candidate.RecoveryCodeId, cancellationToken)
                    .ConfigureAwait(false))
            {
                // 同時に使われた。**二度は通さない**
                break;
            }

            await store.RecordSuccessAsync(user.AdminUserId, cancellationToken).ConfigureAwait(false);
            logger.LogWarning(
                "復旧コードでログインした（AdminUserId={AdminUserId}）。2 要素の登録し直しを促す",
                user.AdminUserId);
            return SecondFactorOutcome.Succeeded;
        }

        await CountFailureAsync(user, cancellationToken).ConfigureAwait(false);
        return SecondFactorOutcome.Invalid;
    }

    /// <summary>2 要素の登録を始める。**まだ保存しない。**</summary>
    /// <remarks>
    /// 認証アプリに読み込ませただけで有効にすると、
    /// **読み込みに失敗していた場合に本人が入れなくなる。**
    /// 一度打ってもらってから <see cref="CompleteTotpEnrollmentAsync"/> で確定する。
    /// </remarks>
    public TotpEnrollment BeginTotpEnrollment(string loginId)
    {
        var secret = totp.GenerateSecret();
        return new TotpEnrollment(secret, TotpService.BuildUri(options.Issuer, loginId, secret));
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
        var (verified, timeStep) = totp.Verify(secretBase32, code);
        if (!verified)
        {
            return null;
        }

        await store.EnableTotpAsync(adminUserId, protector.Protect(secretBase32), cancellationToken)
            .ConfigureAwait(false);

        // **登録に使った時間枠はもう使えない。** そのままログインへ流用させない
        await store.TryConsumeTotpTimeStepAsync(adminUserId, timeStep, cancellationToken)
            .ConfigureAwait(false);

        // **ここまで来たらログインが 1 回通ったのと同じ。**
        // 合言葉と使い捨てパスワードの両方が揃っており、この後 `Admin.Session` になる。
        // 記録しないと、**入れているのに「一度も入っていない」ように見える**
        await store.RecordSuccessAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        var codes = RecoveryCode.Generate();
        await store.ReplaceRecoveryCodesAsync(
            adminUserId,
            codes.Select(value => hasher.Hash(RecoveryCode.Normalize(value))).ToList(),
            cancellationToken).ConfigureAwait(false);

        return codes;
    }

    /// <summary>最初の管理者を作る。**まだ 1 人も居ないときだけ通す。**</summary>
    /// <remarks>
    /// 既定の合言葉を仕込むより、**空の状態から利用者に作らせる**方が安全。
    /// 変え忘れた既定値が残らない。
    /// </remarks>
    public async Task<AdminUser?> TryCreateFirstAdministratorAsync(
        string loginId,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!await store.IsEmptyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var user = new AdminUser
        {
            AdminUserId = Guid.NewGuid(),
            LoginId = loginId,
            PasswordHash = hasher.Hash(password),
            Role = AdminRole.Administrator,
        };

        await store.CreateAsync(user, cancellationToken).ConfigureAwait(false);
        // **利用者が書いた文字列をそのままログへ出さない。**
        // 改行を含めれば、ログの行を偽装できる（CodeQL の cs/log-forging）
        logger.LogInformation("最初の管理者を作った（LoginId={LoginId}）", LogSafe.Text(loginId));
        return user;
    }

    private async Task CountFailureAsync(AdminUser user, CancellationToken cancellationToken)
    {
        var failures = await store.RecordFailureAsync(user.AdminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (failures < options.MaxFailedAttempts)
        {
            return;
        }

        var lockedUntil = timeProvider.GetUtcNow().UtcDateTime + options.LockoutDuration;
        await store.LockAsync(user.AdminUserId, lockedUntil, cancellationToken).ConfigureAwait(false);
        logger.LogWarning(
            "失敗が続いたので締め出した（AdminUserId={AdminUserId}、{LockedUntil} まで）",
            user.AdminUserId,
            lockedUntil);
    }
}
