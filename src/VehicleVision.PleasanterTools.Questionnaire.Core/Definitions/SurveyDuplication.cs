using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>アンケートの定義を丸ごと写す（Issue #46）。</summary>
/// <remarks>
/// <para>
/// **ここで扱うのは定義だけ。** 公開用 ID・書き込み先のサイト・公開状態・公開済みの版は
/// 定義の外（<c>Surveys</c> の行）にあり、**写さずに複製のたびへ決め直す。**
/// </para>
/// <para>
/// **ページと設問の識別子は写す。** 分岐（<see cref="Page.Next"/> /
/// <see cref="Choice.Next"/> / <see cref="Question.VisibleWhen"/>）と
/// マッピングがこの識別子を指しており、振り直すと参照先を全部書き換えることになる。
/// **識別子はアンケートの中でだけ一意**なので、写しても衝突しない
/// （<c>M0003_SurveyDraft</c>）。
/// </para>
/// </remarks>
public static class SurveyDuplication
{
    /// <summary>題名の後ろに付ける文字列。</summary>
    /// <remarks>
    /// **操作した人の言語ではなく、題名の言語で選ぶ。**
    /// 英語の題名に「のコピー」が付くと、回答者に見える文字列が混ざる。
    /// **ここに無い言語には既定の言語のものを付ける**（<c>_documents/多言語対応方針.md</c>）。
    /// </remarks>
    private static readonly FrozenDictionary<string, string> CopySuffixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LocalizedText.DefaultLanguage] = "のコピー",
            ["en"] = " (copy)",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>複製した定義を作る。</summary>
    /// <param name="source">元の定義。**変えない。**</param>
    /// <param name="newSurveyId">複製先の内部 ID。</param>
    /// <remarks>
    /// **<c>with</c> で丸ごと写し、変えるものだけを並べる。**
    /// 項目を 1 つずつ並べて写すと、後から足した項目（分岐がそうだった）を
    /// 写し漏らしても気付けない。
    /// </remarks>
    public static SurveyDefinition Copy(SurveyDefinition source, string newSurveyId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(newSurveyId);

        return source with
        {
            SurveyId = newSurveyId,
            // **公開済みの版は写さないので、次に公開されるのは 1 版目**
            Version = 1,
            Title = CopyTitle(source.Title),
        };
    }

    /// <summary>「〜のコピー」にした題名を作る。</summary>
    /// <remarks>**持っている言語のすべてに付ける。** 片方だけ元の題名のままにしない。</remarks>
    public static LocalizedText CopyTitle(LocalizedText title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in title.Languages)
        {
            copied[language] = title.Get(language) + SuffixOf(language);
        }

        // **言語をひとつも持たない題名でも、複製だと分かるようにする**
        if (copied.Count == 0)
        {
            copied[LocalizedText.DefaultLanguage] = SuffixOf(LocalizedText.DefaultLanguage);
        }

        return new LocalizedText(copied);
    }

    private static string SuffixOf(string language) =>
        CopySuffixes.GetValueOrDefault(language, CopySuffixes[LocalizedText.DefaultLanguage]);
}
