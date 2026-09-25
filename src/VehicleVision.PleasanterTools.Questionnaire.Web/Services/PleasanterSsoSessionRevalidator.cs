using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Pleasanter のログインで入ったことを表す claim（Issue #464）。</summary>
/// <remarks>
/// **本アプリのセッション（サーバ側に保存する中身）に載せる。** cookie には載らない
/// （<see cref="AdminSessionManager"/> は cookie へセッション ID だけを渡す）。
/// </remarks>
public static class PleasanterSsoClaims
{
    /// <summary>Pleasanter のテナント ID。</summary>
    public const string TenantId = "q.pleasanter.tenantid";

    /// <summary>Pleasanter の利用者 ID。**再検証で「同じ人か」を見るのに使う。**</summary>
    public const string UserId = "q.pleasanter.userid";

    /// <summary>Pleasanter に本人を確かめた時刻（UNIX 秒）。</summary>
    public const string VerifiedAt = "q.pleasanter.verifiedat";

    /// <summary>この接頭辞の claim は、2 要素の途中状態からログイン後へ引き継ぐ。</summary>
    public const string Prefix = "q.pleasanter.";

    /// <summary>確かめた本人から claim を作る。</summary>
    public static IReadOnlyList<Claim> Create(PleasanterIdentity identity, DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return
        [
            new Claim(TenantId, identity.TenantId.ToString(CultureInfo.InvariantCulture)),
            new Claim(UserId, identity.UserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(VerifiedAt, verifiedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>途中状態から引き継ぐ claim を取り出す。</summary>
    public static IReadOnlyList<Claim> Carry(ClaimsPrincipal? principal) =>
        principal?.Claims
            .Where(claim => claim.Type.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .ToList()
        ?? [];

    /// <summary>Pleasanter のログインで入ったセッションか。入ったなら中身を読む。</summary>
    public static bool TryRead(
        ClaimsPrincipal? principal,
        out int tenantId,
        out int userId,
        out DateTimeOffset verifiedAt)
    {
        tenantId = 0;
        userId = 0;
        verifiedAt = DateTimeOffset.MinValue;

        if (principal?.FindFirst(UserId) is null)
        {
            return false;
        }

        // **読めない値は「確かめたのは大昔」として扱う。** 再検証へ回して、通らなければ落とす
        _ = int.TryParse(principal.FindFirst(TenantId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out tenantId);
        _ = int.TryParse(principal.FindFirst(UserId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out userId);
        if (long.TryParse(
                principal.FindFirst(VerifiedAt)?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds is > 0 and < 253402300800)
        {
            verifiedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return true;
    }
}

/// <summary>再検証の結果。</summary>
public enum PleasanterSsoRevalidation
{
    /// <summary>Pleasanter のログインで入ったセッションではない。何もしない。</summary>
    NotApplicable,

    /// <summary>まだ間隔内、または問い合わせて同じ人だった。**通す。**</summary>
    Valid,

    /// <summary>Pleasanter でログアウト済み・別人・機能が無効。**本アプリからも落とす。**</summary>
    Rejected,

    /// <summary>Pleasanter に問い合わせられなかった。**本アプリからも落とし、503 を返す。**</summary>
    UpstreamError,
}

/// <summary>Pleasanter のログインで入ったセッションを、一定間隔で Pleasanter に確かめ直す（Issue #464）。</summary>
/// <remarks>
/// <para>
/// **呼ぶのは <see cref="AdminSessionGuard"/>**（本アプリのセッションを要求ごとに見直すところ）。
/// 通らなければそこで認証を失敗させ、セッションを消す。
/// </para>
/// <para>
/// **最後に確かめた時刻はプロセスの中だけで覚える。** 複数インスタンスでは、
/// それぞれが間隔ごとに問い合わせる（問い合わせが増えるだけで、緩くはならない）。
/// 起点はセッションに載せたログイン時の時刻。
/// </para>
/// <para>
/// ⚠️ **問い合わせられなかったときにセッションを延ばさない。** Pleasanter が落ちている間に
/// Pleasanter 側でログアウト・無効化されても気付けないため、**本アプリからも落とす。**
/// </para>
/// </remarks>
public sealed class PleasanterSsoSessionRevalidator(
    IPleasanterSsoOptionsProvider optionsProvider,
    IPleasanterSessionVerifier verifier,
    TimeProvider timeProvider,
    ILogger<PleasanterSsoSessionRevalidator> logger)
{
    /// <summary>問い合わせられずに落としたことを、同じ要求の後段（401 を返すところ）へ伝える印。</summary>
    public const string UpstreamErrorItemKey = "questionnaire:pleasanter_sso_upstream_error";

    /// <summary>覚えておく件数の目安。**超えたら古いものを捨てる。**</summary>
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> lastVerified = new();

    /// <summary>要求のセッションを必要なら確かめ直す。</summary>
    public async Task<PleasanterSsoRevalidation> RevalidateAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        Guid adminSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);

        if (!PleasanterSsoClaims.TryRead(principal, out var tenantId, out var userId, out var verifiedAt))
        {
            return PleasanterSsoRevalidation.NotApplicable;
        }

        var options = (await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false)).Options;
        if (!options.Enabled)
        {
            // **確かめる手段が無いセッションは残さない。** 機能を止めたら、それで入った人も落とす
            logger.LogInformation(
                "Pleasanter のシングルサインオンが無効になったため、それで入ったセッション {SessionId} を終了します。",
                adminSessionId);
            Forget(adminSessionId);
            return PleasanterSsoRevalidation.Rejected;
        }

        var now = timeProvider.GetUtcNow();
        if (lastVerified.TryGetValue(adminSessionId, out var remembered) && remembered > verifiedAt)
        {
            verifiedAt = remembered;
        }

        if (now - verifiedAt < options.RevalidateInterval)
        {
            return PleasanterSsoRevalidation.Valid;
        }

        var result = await verifier.VerifyAsync(context.Request.Headers.Cookie, options, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case PleasanterSessionStatus.Authenticated
                when result.Identity is { } identity
                    && identity.TenantId == tenantId
                    && identity.UserId == userId:
                Remember(adminSessionId, now);
                return PleasanterSsoRevalidation.Valid;

            case PleasanterSessionStatus.Authenticated:
                // **別人に入れ替わっている。** Pleasanter で別の人がログインし直した
                logger.LogWarning(
                    "Pleasanter のログインが別の利用者に替わったため、セッション {SessionId} を終了します。",
                    adminSessionId);
                Forget(adminSessionId);
                return PleasanterSsoRevalidation.Rejected;

            case PleasanterSessionStatus.NotAllowed:
            case PleasanterSessionStatus.Unauthenticated:
                logger.LogInformation(
                    "Pleasanter でのログインまたは所属が許可されないため、セッション {SessionId} を終了します（{Reason}）。",
                    adminSessionId,
                    result.Reason);
                Forget(adminSessionId);
                return PleasanterSsoRevalidation.Rejected;

            default:
                logger.LogWarning(
                    "Pleasanter に確かめ直せなかったため、セッション {SessionId} を終了します（{Reason}）。",
                    adminSessionId,
                    result.Reason);
                Forget(adminSessionId);
                return PleasanterSsoRevalidation.UpstreamError;
        }
    }

    /// <summary>覚えた時刻を捨てる。</summary>
    public void Forget(Guid adminSessionId) => lastVerified.TryRemove(adminSessionId, out _);

    private void Remember(Guid adminSessionId, DateTimeOffset now)
    {
        lastVerified[adminSessionId] = now;

        if (lastVerified.Count <= PruneThreshold)
        {
            return;
        }

        // **セッションの寿命を過ぎたものは要らない。** 消されたセッションの分が溜まり続けないように
        var threshold = now - Endpoints.AdminAuthSchemes.SessionLifetime;
        foreach (var (key, value) in lastVerified)
        {
            if (value < threshold)
            {
                lastVerified.TryRemove(key, out _);
            }
        }
    }
}
