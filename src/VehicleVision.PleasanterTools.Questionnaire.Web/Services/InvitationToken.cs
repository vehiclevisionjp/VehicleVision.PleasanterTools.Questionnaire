using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>管理者の招待に使う 1 回限りのトークン。</summary>
/// <remarks>
/// <para>
/// **既定の合言葉を配らないための道具**（<c>_documents/非機能設計.md</c> 1 章）。
/// 招いた側はこの値を 1 度だけ受け取り、招かれた側が合言葉を自分で決める。
/// </para>
/// <para>
/// **合言葉と違って PBKDF2 を使わない。** ここで守る値は
/// <see cref="TokenBytes"/> バイトの暗号論的乱数で、辞書攻撃の対象にならない。
/// それより、**ハッシュで直に引けること**が要る。
/// PBKDF2 にすると全件を総当たりで照合することになり、
/// **認証を通っていない入口を重い計算に晒す**（そこが弱点になる）。
/// </para>
/// </remarks>
public static class InvitationToken
{
    /// <summary>トークンの長さ。**推測できない量を持たせる。**</summary>
    private const int TokenBytes = 32;

    /// <summary>新しいトークンを作る。**返せるのはこの時だけ。**</summary>
    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);

        // URL や QR に載せても壊れない形にする
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>保存・照合に使うハッシュ。</summary>
    /// <remarks>**元の値は保存しない。** 表を読めた人が招待を使えては意味が無い。</remarks>
    public static string HashOf(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}
