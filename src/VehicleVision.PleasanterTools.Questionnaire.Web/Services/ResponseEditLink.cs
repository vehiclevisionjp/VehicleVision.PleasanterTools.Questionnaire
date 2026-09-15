using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答の再編集リンクに使うトークン（Issue #202）。</summary>
/// <remarks>
/// <para>
/// **回答本体の <c>ResponseToken</c> をメールへ載せないための値。**
/// こちらが漏れても、失効させれば回答本体は生き残る。
/// </para>
/// <para>
/// **リンクは URL の断片（<c>#</c> の後ろ）に置く。**
/// ⚠️ **断片はサーバへ送られない**ので、**Web サーバのアクセスログにも、
/// 経路の値を丸ごと書く監査ログにも載らない。**
/// 画面が読み取って、本文へ入れて引き換える。
/// </para>
/// <para>
/// **ハッシュは PBKDF2 にしない**（招待のトークンと同じ理由）。
/// 守る値は暗号論的乱数で辞書攻撃の対象にならず、
/// **引き換えの口を重い計算に晒す方が危ない。**
/// </para>
/// </remarks>
public static class ResponseEditLink
{
    /// <summary>トークンの長さ。</summary>
    private const int TokenBytes = 32;

    /// <summary>断片に付ける名前。**画面と揃えること。**</summary>
    public const string FragmentKey = "e";

    /// <summary>新しいトークンを作る。**返せるのはこの時だけ。**</summary>
    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);

        // URL に載せても壊れない形にする
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>保存・照合に使うハッシュ。**元の値は保存しない。**</summary>
    public static string HashOf(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    /// <summary>メールに載せる URL を組み立てる。</summary>
    /// <param name="baseUrl">起点。**要求の <c>Host</c> からは作らない**（Issue #189）。</param>
    /// <param name="publicId">アンケートの公開 ID。</param>
    /// <param name="token">トークン。</param>
    public static string UrlOf(string baseUrl, string publicId, string token) =>
        $"{baseUrl.TrimEnd('/')}/f/{Uri.EscapeDataString(publicId)}"
        + $"#{FragmentKey}={Uri.EscapeDataString(token)}";
}
