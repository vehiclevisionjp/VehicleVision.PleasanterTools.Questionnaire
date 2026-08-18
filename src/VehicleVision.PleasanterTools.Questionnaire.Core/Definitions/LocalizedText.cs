using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>言語コードをキーにした表示文字列。</summary>
/// <remarks>
/// 使うのが日本語だけでも、器は最初から用意する。
/// 後から多言語化すると全テーブルの文字列列を作り直すことになる
/// （<c>_documents/データモデル設計.md</c> 2.3）。
/// </remarks>
public sealed class LocalizedText
{
    /// <summary>言語コードが見つからないときに使う既定の言語。</summary>
    public const string DefaultLanguage = "ja";

    private readonly FrozenDictionary<string, string> _byLanguage;

    public LocalizedText(IReadOnlyDictionary<string, string> byLanguage)
    {
        ArgumentNullException.ThrowIfNull(byLanguage);
        _byLanguage = byLanguage.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>日本語だけを持つ <see cref="LocalizedText"/> を作る。</summary>
    public static LocalizedText Japanese(string text) =>
        new(new Dictionary<string, string> { [DefaultLanguage] = text });

    /// <summary>指定した言語の文字列を返す。無ければ既定の言語、それも無ければ空文字。</summary>
    public string Get(string? language)
    {
        if (!string.IsNullOrEmpty(language) && _byLanguage.TryGetValue(language, out var text))
        {
            return text;
        }

        return _byLanguage.TryGetValue(DefaultLanguage, out var fallback) ? fallback : string.Empty;
    }

    /// <summary>保持している言語コード。</summary>
    public IReadOnlyCollection<string> Languages => _byLanguage.Keys;
}
