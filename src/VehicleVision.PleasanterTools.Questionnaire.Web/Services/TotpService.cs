using System.Net;
using System.Security.Cryptography;
using OtpNet;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>時刻に基づく使い捨てパスワード（RFC 6238）。</summary>
/// <remarks>
/// <para>
/// **自前で実装しない。** 実装の細部を外すと、当たらないか、あるいは通り過ぎる。
/// Otp.NET（MIT）に任せる。
/// </para>
/// <para>
/// **共有鍵は暗号化して保存する**（<see cref="SecretProtector"/>）。
/// </para>
/// </remarks>
public sealed class TotpService
{
    /// <summary>共有鍵の長さ。RFC 4226 が最低 128 ビット、推奨 160 ビットとする。</summary>
    private const int SecretBytes = 20;

    /// <summary>前後に許す時間の枠。</summary>
    /// <remarks>
    /// **端末の時計のずれを吸収するため、前後 1 枠（±30 秒）だけ許す。**
    /// 広げるほど総当たりが通りやすくなる。
    /// </remarks>
    private static readonly VerificationWindow Window = new(previous: 1, future: 1);

    /// <summary>新しい共有鍵を Base32 で作る。</summary>
    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(SecretBytes));

    /// <summary>認証アプリに読ませる <c>otpauth://</c> の URI を作る。</summary>
    /// <param name="issuer">サービス名。認証アプリの一覧に出る。</param>
    /// <param name="loginId">利用者の識別子。</param>
    /// <param name="secretBase32">Base32 の共有鍵。</param>
    public static string BuildUri(string issuer, string loginId, string secretBase32)
    {
        var label = $"{issuer}:{loginId}";
        return "otpauth://totp/"
            + WebUtility.UrlEncode(label)
            + "?secret=" + secretBase32
            + "&issuer=" + WebUtility.UrlEncode(issuer)
            + "&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>入力された数字を照合する。</summary>
    /// <returns>合っていたかどうかと、合った時間枠。</returns>
    /// <remarks>
    /// **同じ時間枠の使い回しは呼び出し側で弾く。**
    /// 30 秒の間は同じ数字が通るので、盗み見られた数字がそのまま使える。
    /// </remarks>
    public (bool Verified, long TimeStep) Verify(string secretBase32, string code)
    {
        if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code))
        {
            return (false, 0);
        }

        byte[] secret;
        try
        {
            secret = Base32Encoding.ToBytes(secretBase32);
        }
        catch (ArgumentException)
        {
            return (false, 0);
        }

        var totp = new Totp(secret);
        var verified = totp.VerifyTotp(code.Trim(), out var timeStep, Window);
        return (verified, timeStep);
    }
}

/// <summary>復旧コードの生成と整形。</summary>
/// <remarks>
/// **端末を失っても入れる道を用意する。** これが無いと、
/// 認証アプリを消した時点で誰も管理画面に入れなくなる。
/// </remarks>
public static class RecoveryCode
{
    /// <summary>配る本数。</summary>
    public const int Count = 10;

    /// <summary>
    /// 使う文字。**紛らわしい文字（0/O・1/I/L）を外す。**
    /// 紙に書き写すことを前提にしている。
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private const int GroupLength = 5;
    private const int Groups = 2;

    /// <summary>復旧コードを 1 組作る。**表示できるのはこの時だけ。**</summary>
    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var index = 0; index < Count; index++)
        {
            var buffer = new char[(GroupLength * Groups) + Groups - 1];
            var position = 0;
            for (var group = 0; group < Groups; group++)
            {
                if (group > 0)
                {
                    buffer[position++] = '-';
                }

                for (var character = 0; character < GroupLength; character++)
                {
                    buffer[position++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }
            }

            codes.Add(new string(buffer));
        }

        return codes;
    }

    /// <summary>入力の揺れを吸収する。**空白と区切りを外し、大文字に揃える。**</summary>
    public static string Normalize(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
