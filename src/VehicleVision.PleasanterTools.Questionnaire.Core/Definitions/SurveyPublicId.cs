using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答用 URL に使う推測不能な値。</summary>
/// <remarks>
/// <para>
/// **サイト ID や <c>SurveyId</c> を URL に出さない**
/// （<c>_documents/データモデル設計.md</c> 3 章）。
/// 順番に並んだ値だと、総当たりで他のアンケートを見つけられる。
/// </para>
/// <para>
/// **作り方を 1 か所にまとめてある。** 作成と複製で別々に書くと、
/// 片方だけ弱い値になっても気付けない（Issue #46）。
/// </para>
/// </remarks>
public static class SurveyPublicId
{
    /// <summary>値の頭に付ける印。**ログで見分けるためだけのもの。**</summary>
    public const string Prefix = "pub-";

    /// <summary>乱数の長さ（バイト）。</summary>
    /// <remarks>
    /// 128 ビット。**総当たりで当てられない長さ**にする一方、
    /// <c>Surveys.PublicId</c> の 64 文字にも収める（印 4 文字 ＋ 16 進 32 文字）。
    /// </remarks>
    private const int ByteLength = 16;

    /// <summary>新しい公開用 ID を作る。</summary>
    /// <remarks>
    /// **暗号論的乱数から作る。** <c>Guid.NewGuid</c> や連番を使わない
    /// （<c>_documents/データモデル設計.md</c> 3 章）。
    /// **公開を止めても、複製しても、使い回さない。**
    /// </remarks>
    public static string Generate() =>
        Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(ByteLength)).ToLowerInvariant();
}
