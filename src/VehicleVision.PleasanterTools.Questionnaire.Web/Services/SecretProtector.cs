using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>DB に置く秘密の暗号化と復号。</summary>
/// <remarks>
/// <para>
/// TOTP の共有鍵は**照合のたびに元の値が要る**のでハッシュにできない。
/// **だからこそ暗号化して置く。平文で置かない**（<c>_documents/データモデル設計.md</c> 2.6）。
/// </para>
/// <para>
/// **Data Protection の鍵束を使わない。** App Service では鍵の保存先が
/// 既定で一時領域になり、**再起動で鍵を失うと登録済みの 2 要素が全部使えなくなる**。
/// 運用者が持つ 1 本の鍵を設定から受け取る形にして、この事故を無くす。
/// </para>
/// <para>
/// **鍵を失うと復号できない。** その場合は復旧コードで入り、2 要素を登録し直す
/// （<c>_documents/リリース手順書.md</c>）。
/// </para>
/// </remarks>
public sealed class SecretProtector
{
    /// <summary>書式の版。**変えるときは <see cref="Unprotect"/> の分岐を足す。**</summary>
    private const string Version = "v1";

    /// <summary>
    /// 文脈に束縛した書式の版（<see cref="Protect(string, string)"/>）。
    /// 文脈（利用者 ID など）を AES-GCM の追加認証データ（AAD）に入れる。**別の文脈では復号できない。**
    /// </summary>
    private const string ContextVersion = "v2";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private readonly byte[] key;

    /// <param name="base64Key">256 ビットの鍵を Base64 にしたもの。</param>
    public SecretProtector(string base64Key) => key = DecodeKey(base64Key);

    /// <summary>設定から受け取った鍵を読む。**書式と長さをここ 1 か所で見る。**</summary>
    /// <remarks>
    /// 同じ設定値は <see cref="SubmissionGuard"/> も使う。
    /// **鍵の検証を写して持たない。** 片方だけ緩いと、そこから短い鍵が入る。
    /// </remarks>
    public static byte[] DecodeKey(string base64Key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(base64Key);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "秘密の鍵は Base64 で与える（QUESTIONNAIRE_SECRET_KEY）", nameof(base64Key), exception);
        }

        if (decoded.Length != KeyBytes)
        {
            throw new ArgumentException(
                $"秘密の鍵は {KeyBytes} バイト（256 ビット）で与える。実際は {decoded.Length} バイト",
                nameof(base64Key));
        }

        return decoded;
    }

    /// <summary>新しい鍵を作る。**設定に書く値を作るためのもの。**</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    /// <summary>新しいトークンを作る。**URL や環境変数へ安全に書ける形にする。**</summary>
    public static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    /// <summary>暗号化して、保存する形の文字列にする。</summary>
    public string Protect(string plaintext) => Protect(plaintext, Version, associatedData: null);

    /// <summary>
    /// <paramref name="context"/> に束縛して暗号化する（管理者の TOTP の共有鍵。Core.Data の <c>ISecretCipher</c> 経由）。
    /// </summary>
    /// <remarks>
    /// **別の文脈では復号できない。** 暗号文を別の管理者の行へ貼り替えても、その管理者の共有鍵としては読めない。
    /// 文脈を持たない形（<see cref="Protect(string)"/>）で暗号化した値は、<see cref="Unprotect(string, string)"/> が
    /// 文脈を問わずに復号する（文脈を持たなかった旧の暗号文を、そのまま読むため）。
    /// </remarks>
    public string Protect(string plaintext, string context)
    {
        ArgumentException.ThrowIfNullOrEmpty(context);
        return Protect(plaintext, ContextVersion, Encoding.UTF8.GetBytes(context));
    }

    private string Protect(string plaintext, string version, byte[]? associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        // **毎回違う値を使う。** 使い回すと同じ平文が同じ暗号文になり、突き合わせられる
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[bytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, bytes, cipher, tag, associatedData);

        return string.Join(
            '$', version, Convert.ToBase64String(nonce), Convert.ToBase64String(tag),
            Convert.ToBase64String(cipher));
    }

    /// <summary>復号する。壊れていたり鍵が違えば <c>null</c>。</summary>
    /// <remarks>**改竄されていれば復号は失敗する**（AES-GCM が検知する）。</remarks>
    public string? Unprotect(string protectedValue) => Unprotect(protectedValue, context: null);

    /// <summary>
    /// 復号する。<see cref="Protect(string, string)"/> で暗号化した値は、<b>同じ文脈でだけ</b>復号できる。
    /// 文脈を持たない形で暗号化した値（旧の暗号文）は、文脈を問わず復号する。壊れていたり鍵・文脈が違えば <c>null</c>。
    /// </summary>
    public string? Unprotect(string protectedValue, string? context)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        var parts = protectedValue.Split('$');
        if (parts.Length != 4 || (parts[0] != Version && parts[0] != ContextVersion))
        {
            return null;
        }

        byte[]? associatedData = null;
        if (parts[0] == ContextVersion)
        {
            // **文脈に束縛した値は、文脈が分からなければ読まない**
            if (string.IsNullOrEmpty(context))
            {
                return null;
            }

            associatedData = Encoding.UTF8.GetBytes(context);
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var cipher = Convert.FromBase64String(parts[3]);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, associatedData);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException
            or ArgumentException)
        {
            return null;
        }
    }
}
