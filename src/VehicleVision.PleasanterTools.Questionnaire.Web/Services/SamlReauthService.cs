using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VehicleVision.PleasanterTools.Questionnaire.Data;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>
/// 署名を確かめた SAML の応答から取り出した、再認証の判断に要る事実だけ（Issue #1034）。
/// </summary>
/// <param name="InResponseTo">応答が指す要求の id。</param>
/// <param name="LoginId">本アプリのログイン ID として使う値（設定に従って取り出したもの）。</param>
/// <param name="AuthnInstant">IdP が利用者を認証した時刻。<b>無ければ <c>null</c>（証明できないので断る）。</b></param>
/// <param name="AuthnContextClassRef">認証の手段の種別。</param>
public sealed record SamlAssertionFacts(
    string? InResponseTo,
    string? LoginId,
    DateTimeOffset? AuthnInstant,
    string? AuthnContextClassRef);

/// <summary>再認証の結果。<b>画面へは区別して返さず、理由は監査へ残す。</b></summary>
public enum SamlReauthOutcome
{
    /// <summary>許可を 1 回分置いた。</summary>
    Granted,

    /// <summary>こちらが出した要求に結び付かない（知らない・期限切れ・すでに使った）。</summary>
    RequestUnknown,

    /// <summary>応答が指す要求と、cookie の要求が違う。</summary>
    RequestMismatch,

    /// <summary>要求を出したのとは別の人で認証された、または利用者が使えない。</summary>
    UserMismatch,

    /// <summary>IdP が認証の時刻を返さなかった。</summary>
    AuthnInstantMissing,

    /// <summary>認証の時刻が、要求を出した時刻より前（IdP が ForceAuthn を守らず、前の認証を流用した）。</summary>
    AuthnInstantStale,

    /// <summary>認証の時刻が未来（時計のずれが許容を超えている）。</summary>
    AuthnInstantInFuture,

    /// <summary>IdP が「前のセッションの流用」と明示した。</summary>
    PreviousSession,
}

/// <summary>
/// SAML の強制再認証（<c>ForceAuthn</c>）で、API キーの発行 1 回分の許可を作る・使う（Issue #1034。親 #1012）。
/// </summary>
/// <remarks>
/// <para>
/// <b>流れ。</b>① 画面の依頼で <see cref="BeginAsync"/>（出す要求の id・利用者・セッション・時刻を預ける）
/// → ② IdP で再認証 → ③ 受け口が署名を確かめた応答の事実を <see cref="CompleteAsync"/> へ渡す
/// → ④ 発行が <see cref="TryConsumeGrantAsync"/> で許可を<b>1 回だけ</b>取り出す。
/// </para>
/// <para>
/// <b>使い捨てにするもの。</b>途中の状態（要求の id）は、<b>①の応答を判定する 1 回で消える</b>
/// （失敗しても消える。同じ応答の再送・総当たりを許さない）。発行の許可は、<b>1 回の発行で消える</b>。
/// 許可は<b>利用者とセッションに結び付ける</b>（別のセッションでは使えない）。期限は短い
/// （<see cref="AdminApiTokenOptions.ReloginWindow"/>）。
/// </para>
/// <para>
/// <b>用途に結び付ける。</b>置き場のキーに用途（<see cref="Purpose"/>）を含める。
/// 将来ほかの操作で再認証を使っても、API キーの発行の許可を流用できない。
/// </para>
/// </remarks>
public sealed class SamlReauthService(
    IAdminReauthStateStore store,
    IAdminUserStore users,
    AdminApiTokenOptions options,
    TimeProvider timeProvider)
{
    /// <summary>許可の用途。API キーの発行。</summary>
    public const string Purpose = "api-token-issue";

    /// <summary>IdP で再認証する間、状態を預けておく長さ（パスワード・2 要素を入れる時間）。</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>IdP と本アプリの時計のずれの許容。<b>これだけ前までの認証の時刻は、要求の後とみなす。</b></summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>「前のセッションの流用」を表す認証の種別（SAML 2.0 の AuthnContext）。</summary>
    private const string PreviousSessionClass = "urn:oasis:names:tc:SAML:2.0:ac:classes:PreviousSession";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record Pending(Guid UserId, string SessionHandle, DateTimeOffset IssuedAt);

    /// <summary>再認証の要求を出すときに、状態を預ける。</summary>
    /// <param name="requestId">出した AuthnRequest の id。</param>
    /// <param name="userId">いまログインしている管理者。</param>
    /// <param name="sessionHandle">いまのセッションの識別子（ハッシュ）。</param>
    public Task BeginAsync(
        string requestId,
        Guid userId,
        string sessionHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentException.ThrowIfNullOrEmpty(sessionHandle);

        var pending = new Pending(userId, sessionHandle, timeProvider.GetUtcNow());
        return store.PutAsync(
            PendingKey(requestId),
            JsonSerializer.Serialize(pending, JsonOptions),
            PendingLifetime,
            cancellationToken);
    }

    /// <summary>署名を確かめた応答の事実を判定し、通れば発行の許可を 1 回分置く。</summary>
    /// <param name="cookieRequestId">ブラウザの cookie が指す要求の id（<b>この要求を出したブラウザ</b>の証）。</param>
    /// <param name="facts">署名・発行者・宛先・期限を確かめた後の応答から取り出した事実。</param>
    public async Task<SamlReauthOutcome> CompleteAsync(
        string cookieRequestId,
        SamlAssertionFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookieRequestId);
        ArgumentNullException.ThrowIfNull(facts);

        if (!string.Equals(facts.InResponseTo, cookieRequestId, StringComparison.Ordinal))
        {
            return SamlReauthOutcome.RequestMismatch;
        }

        // ⚠️ **判定の前に消す。** 結果が失敗でも、この要求は二度と使えない（再送・総当たりを許さない）
        var stored = await store.TakeAsync(PendingKey(cookieRequestId), cancellationToken).ConfigureAwait(false);
        var pending = stored is null ? null : JsonSerializer.Deserialize<Pending>(stored, JsonOptions);
        if (pending is null)
        {
            return SamlReauthOutcome.RequestUnknown;
        }

        // **要求を出した人と、IdP が認証した人が同じこと。** 別の人で再認証して他人の許可を作らせない
        if (string.IsNullOrWhiteSpace(facts.LoginId))
        {
            return SamlReauthOutcome.UserMismatch;
        }

        var found = (await users.FindForLoginAsync(facts.LoginId.Trim(), cancellationToken).ConfigureAwait(false))?.User;
        var now = timeProvider.GetUtcNow();
        if (found is null || found.Id != pending.UserId || found.IsDisabled || found.IsLocked(now))
        {
            return SamlReauthOutcome.UserMismatch;
        }

        if (string.Equals(facts.AuthnContextClassRef, PreviousSessionClass, StringComparison.Ordinal))
        {
            return SamlReauthOutcome.PreviousSession;
        }

        // ⚠️ **ForceAuthn を IdP が守ったかを、認証の時刻で確かめる。**
        // IdP が黙って前のセッションで通すと、時刻は要求より前のまま
        if (facts.AuthnInstant is not { } instant)
        {
            return SamlReauthOutcome.AuthnInstantMissing;
        }

        if (instant < pending.IssuedAt - ClockSkew)
        {
            return SamlReauthOutcome.AuthnInstantStale;
        }

        if (instant > now + ClockSkew)
        {
            return SamlReauthOutcome.AuthnInstantInFuture;
        }

        await store.PutAsync(
            GrantKey(pending.UserId, pending.SessionHandle),
            "1",
            options.ReloginWindow,
            cancellationToken).ConfigureAwait(false);
        return SamlReauthOutcome.Granted;
    }

    /// <summary>発行の許可を 1 回だけ取り出す。<b>取り出せたら <c>true</c>、無い・期限切れ・使用済みなら <c>false</c>。</b></summary>
    public async Task<bool> TryConsumeGrantAsync(
        Guid userId,
        string sessionHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionHandle);
        return await store.TakeAsync(GrantKey(userId, sessionHandle), cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>キーを作る。<b>値はハッシュにして置く</b>（要求の id やセッションの識別子をそのままキーにしない）。</summary>
    private static string PendingKey(string requestId) => $"pending:{Hash(requestId)}";

    private static string GrantKey(Guid userId, string sessionHandle) =>
        $"grant:{Purpose}:{userId:N}:{Hash(sessionHandle)}";

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
