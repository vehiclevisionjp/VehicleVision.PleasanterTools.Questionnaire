using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Localization;

/// <summary>本アプリが画面に出せる言語。</summary>
/// <remarks>
/// <para>
/// **落とし先は必ず <see cref="LocalizedText.DefaultLanguage"/>。**
/// 未翻訳の設問が 1 つあっただけで画面が空になっては困る
/// （<c>_documents/データモデル設計.md</c> 2.3）。
/// </para>
/// <para>
/// **ここに無い言語は「指定されなかった」ものとして扱う。** 例外にしない。
/// 言語の指定は誰でも自由に書ける値なので、
/// **知らない値が来たときに落ちる作りにすると、そこが攻撃面になる。**
/// </para>
/// </remarks>
public static class SupportedLanguages
{
    /// <summary>選べる言語。**先頭が既定。**</summary>
    public static readonly ImmutableArray<string> All = [LocalizedText.DefaultLanguage, "en"];

    /// <summary>未翻訳・未指定のときに使う言語。</summary>
    public const string Default = LocalizedText.DefaultLanguage;

    /// <summary>対応している言語か。</summary>
    public static bool IsSupported(string? language) => Normalize(language) is not null;

    /// <summary>言語タグを本アプリの言語コードへ寄せる。対応していなければ <c>null</c>。</summary>
    /// <remarks>
    /// **地域まで見ない。** <c>en-US</c> も <c>en-GB</c> も <c>en</c> として扱う。
    /// 文言を地域ごとに分けると、翻訳の抜けを見つけるのが一気に難しくなる。
    /// </remarks>
    public static string? Normalize(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return null;
        }

        var primary = languageTag.Trim();
        var separator = primary.IndexOfAny(['-', '_']);
        if (separator >= 0)
        {
            primary = primary[..separator];
        }

        foreach (var supported in All)
        {
            if (string.Equals(primary, supported, StringComparison.OrdinalIgnoreCase))
            {
                return supported;
            }
        }

        return null;
    }

    /// <summary><c>Accept-Language</c> ヘッダから、対応している言語を 1 つ選ぶ。</summary>
    /// <remarks>
    /// <para>
    /// <c>en-US,en;q=0.9,ja;q=0.8</c> のような値を、**品質値の大きい順**に見て
    /// 最初に対応しているものを返す。ひとつも無ければ <c>null</c>。
    /// </para>
    /// <para>
    /// **この値を保存しない。** 回答者は完全匿名であり、
    /// 言語の好みは回答者を絞り込む手掛かりになる
    /// （<c>_documents/多言語対応方針.md</c>）。
    /// </para>
    /// </remarks>
    public static string? FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var candidates = new List<(string Language, double Quality, int Order)>();
        var order = 0;

        foreach (var entry in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(';', StringSplitOptions.TrimEntries);
            var tag = parts[0];
            if (tag == "*")
            {
                continue;
            }

            // 品質値が読めないものは 1.0 として扱う（既定値と同じ）
            var quality = 1.0d;
            foreach (var parameter in parts.Skip(1))
            {
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(
                        parameter[2..],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    quality = parsed;
                }
            }

            // **品質値 0 は「要らない」の意味。** 候補に入れない
            if (quality <= 0)
            {
                continue;
            }

            if (Normalize(tag) is { } language)
            {
                candidates.Add((language, quality, order));
            }

            order++;
        }

        return candidates
            .OrderByDescending(candidate => candidate.Quality)
            .ThenBy(candidate => candidate.Order)
            .Select(candidate => candidate.Language)
            .FirstOrDefault();
    }

    /// <summary>明示の指定と <c>Accept-Language</c> から、使う言語を決める。</summary>
    /// <param name="requested">URL や設定で明示された言語。**一番強い。**</param>
    /// <param name="acceptLanguageHeader">要求の <c>Accept-Language</c>。</param>
    /// <remarks>
    /// **明示 → <c>Accept-Language</c> → 既定**の順。
    /// 明示が対応外の値なら、無かったものとして次を見る。
    /// </remarks>
    public static string Resolve(string? requested, string? acceptLanguageHeader) =>
        Normalize(requested)
        ?? FromAcceptLanguage(acceptLanguageHeader)
        ?? Default;
}
