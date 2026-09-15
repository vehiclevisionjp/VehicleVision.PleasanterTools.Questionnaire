using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>パスワードのハッシュ。</summary>
/// <remarks>
/// <para>
/// **ハッシュのみを保存する。平文・可逆暗号にしない**
/// （<c>_documents/データモデル設計.md</c> 2.6）。
/// </para>
/// <para>
/// **書式に版を持たせる。** 反復回数やアルゴリズムを将来上げたときに、
/// 既存の利用者を締め出さずに移行できる。
/// </para>
/// </remarks>
public sealed class PasswordHasher
{
    /// <summary>書式の版。**上げるときは <see cref="Verify"/> の分岐を足す。**</summary>
    private const string Version = "v1";

    private const KeyDerivationPrf Prf = KeyDerivationPrf.HMACSHA512;
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>保存する形の文字列を作る。</summary>
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);

        return string.Join('$', Version, Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>照合する。</summary>
    /// <returns>
    /// 一致したかどうかと、**書式が古いので作り直すべきか**。
    /// 古い場合はログイン成功時に新しい書式で保存し直す。
    /// </returns>
    public (bool Verified, bool NeedsRehash) Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored))
        {
            return (false, false);
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Version || !int.TryParse(parts[1], out var iterations))
        {
            return (false, false);
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        var actual = Derive(password, salt, iterations);

        // **一定時間で比較する。** 差が出る比較にするとハッシュを推測されうる
        var verified = CryptographicOperations.FixedTimeEquals(actual, expected);
        return (verified, verified && iterations < Iterations);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        KeyDerivation.Pbkdf2(password, salt, Prf, iterations, HashBytes);
}
