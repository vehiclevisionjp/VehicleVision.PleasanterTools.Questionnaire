using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>検索用トークンを確かめた結果。</summary>
/// <param name="IsValid">署名が合い、対象のフォームのもので、期限内か。</param>
/// <param name="SessionId">
/// 回答者の枠を数える識別子。**署名の中の乱数で、個人を指さない。長期保存しない。**
/// 有効なときだけ入る。
/// </param>
public readonly record struct LookupSessionCheck(bool IsValid, string? SessionId);

/// <summary>ルックアップの検索に添える、フォーム限定・署名つきの短命トークン（Issue #832・仕様書 4.1 節）。</summary>
/// <remarks>
/// <para>
/// **目的を分ける。** 送信チケット（<see cref="SubmissionGuard"/>）や資産アクセス許可とは
/// 鍵の派生ラベルも版も別。**どれかで署名したものが、ほかでは通らない。検索に送信権限を持たせない。**
/// </para>
/// <para>
/// 入るのは公開 ID・乱数・発行時刻だけ。IP・UA・指紋は入れない。サーバー側に何も覚えず、
/// 回答者を突き合わせる手掛かりを残さない（<c>_documents/多言語対応方針.md</c> の匿名性）。
/// 乱数は回答者の枠を数えるための匿名セッション識別子で、トークンの期限（既定 30 分）を超えて使われない。
/// </para>
/// <para>
/// ⚠️ **トークンはログ・例外・応答の本文へ出さない。** 乱数は枠の数え先のキーにだけ使う。
/// </para>
/// </remarks>
public sealed class LookupSessionTokens
{
    /// <summary>書式の版。**変えるときは検証側の分岐を足す。**</summary>
    private const string Version = "l1";

    private const int NonceBytes = 16;

    /// <summary>鍵を用途で分けるためのラベル。</summary>
    private static readonly byte[] KeyLabel =
        Encoding.UTF8.GetBytes("questionnaire:lookup-session:v1");

    private readonly byte[] key;
    private readonly LookupPublicOptions options;
    private readonly TimeProvider time;

    /// <param name="base64Key">256 ビットの鍵の Base64（<c>QUESTIONNAIRE_SECRET_KEY</c>）。</param>
    public LookupSessionTokens(string base64Key, LookupPublicOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        this.key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, SecretProtector.DecodeKey(base64Key), 32, info: KeyLabel);
        this.options = options;
        this.time = time;
    }

    /// <summary>有効期間。回答画面へ知らせるのに使う。</summary>
    public TimeSpan Lifetime => options.SessionLifetime;

    /// <summary>トークンを発行する。**DB を見ない**（応答の速さで公開 ID の実在を漏らさない）。</summary>
    public string Issue(string publicId)
    {
        ArgumentException.ThrowIfNullOrEmpty(publicId);

        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(NonceBytes));
        return string.Join(
            '.',
            Version,
            issuedAt.ToString(CultureInfo.InvariantCulture),
            nonce,
            Sign(publicId, nonce, issuedAt));
    }

    /// <summary>トークンを確かめる。</summary>
    /// <remarks>書式の違い・署名の不一致・別のフォーム・期限切れ・未来の発行時刻は、区別せず無効にする。</remarks>
    public LookupSessionCheck Verify(string? token, string publicId)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(publicId) || token.Length > 256)
        {
            return default;
        }

        var parts = token.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !long.TryParse(
                parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt))
        {
            return default;
        }

        var expected = Sign(publicId, parts[2], issuedAt);

        // **一致の判定に掛かる時間で中身を漏らさない**
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[3])))
        {
            return default;
        }

        var elapsed = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAt);
        return elapsed >= TimeSpan.Zero && elapsed <= options.SessionLifetime
            ? new LookupSessionCheck(true, parts[2])
            : default;
    }

    private string Sign(string publicId, string nonce, long issuedAt)
    {
        // **区切りに改行を使い、長さの違いで同じ入力にならないようにする**
        var payload = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            Version,
            publicId,
            nonce,
            issuedAt.ToString(CultureInfo.InvariantCulture)));
        return Base64Url.EncodeToString(HMACSHA256.HashData(key, payload));
    }
}
