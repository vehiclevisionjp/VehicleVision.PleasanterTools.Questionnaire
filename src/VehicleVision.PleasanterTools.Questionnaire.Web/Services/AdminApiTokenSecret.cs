using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>発行した直後のトークン。<b>平文を持つのは、この値を返す一瞬だけ。</b></summary>
/// <param name="Plain">平文のトークン。<b>ログ・監査・例外メッセージに絶対に出さない。</b></param>
/// <param name="Prefix">識別用の接頭辞（一覧・監査へ出してよい）。</param>
/// <param name="Hash">保存するハッシュ（小文字の 16 進 64 文字）。</param>
public sealed record AdminApiTokenSecretValue(string Plain, string Prefix, string Hash)
{
    /// <summary>⚠️ <b>平文を文字列化に載せない</b>（ログへ渡されても漏れないように）。</summary>
    public override string ToString() => $"AdminApiTokenSecretValue {{ Prefix = {Prefix} }}";
}

/// <summary>管理 API の個人トークンの値の作り方と、保存用のハッシュ（Issue #1025）。</summary>
/// <remarks>
/// <para>
/// 形式は <c>qat_</c> ＋ 識別用の 8 文字（16 進）＋ <c>_</c> ＋ 乱数 256 ビット（Base64URL 43 文字）。
/// <b>固定の接頭辞</b>で、リポジトリ・ログへの混入を見つけやすくする（秘密スキャン・DLP）。
/// </para>
/// <para>
/// <b>PBKDF2 は使わない。</b>256 ビットの乱数に辞書攻撃は成立せず、ハッシュで直に引けることが要る
/// （全件照合にすると、認証前の入口を重い計算に晒す）。<see cref="InvitationToken"/> と同じ判断。
/// </para>
/// </remarks>
public static class AdminApiTokenSecret
{
    /// <summary>トークンの固定の接頭辞。</summary>
    public const string Scheme = "qat_";

    private const int PublicPartBytes = 4;
    private const int SecretBytes = 32;

    /// <summary>識別用の接頭辞の長さ（<c>qat_</c> ＋ 16 進 8 文字）。</summary>
    public const int PrefixLength = 12;

    /// <summary>新しいトークンを作る。<b>平文を返せるのはこの時だけ。</b></summary>
    public static AdminApiTokenSecretValue Create()
    {
        var publicPart = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PublicPartBytes));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var plain = $"{Scheme}{publicPart}_{secret}";
        return new AdminApiTokenSecretValue(plain, plain[..PrefixLength], HashOf(plain));
    }

    /// <summary>保存・照合に使うハッシュ（SHA-256。小文字の 16 進 64 文字）。</summary>
    /// <remarks><b>元の値は保存しない。</b>表を読めた人がトークンを使えては意味が無い。</remarks>
    public static string HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>トークンの形か（接頭辞と長さだけを見る。<b>値の正しさは DB で確かめる</b>）。</summary>
    public static bool LooksLikeToken(string? value) =>
        value is { Length: > PrefixLength + 1 and <= 128 }
        && value.StartsWith(Scheme, StringComparison.Ordinal);
}
