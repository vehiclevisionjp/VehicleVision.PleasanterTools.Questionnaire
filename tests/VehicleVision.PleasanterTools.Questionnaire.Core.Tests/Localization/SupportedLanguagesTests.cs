using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Localization;

public class SupportedLanguagesTests
{
    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("en-US", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("en_GB", "en")]
    [InlineData("  en-us  ", "en")]
    public void 地域を落として言語コードに寄せる(string tag, string expected)
    {
        Assert.Equal(expected, SupportedLanguages.Normalize(tag));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("zh-Hans")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 対応していない言語はnullになる(string? tag)
    {
        // **例外にしない。** 言語の指定は誰でも自由に書ける値なので、
        // 知らない値で落ちる作りにするとそこが攻撃面になる
        Assert.Null(SupportedLanguages.Normalize(tag));
    }

    [Fact]
    public void AcceptLanguageは品質値の大きい順に見る()
    {
        Assert.Equal("en", SupportedLanguages.FromAcceptLanguage("fr;q=1.0, ja;q=0.5, en;q=0.9"));
    }

    [Fact]
    public void 品質値が同じなら先に書かれた方を採る()
    {
        Assert.Equal("en", SupportedLanguages.FromAcceptLanguage("en, ja"));
    }

    [Fact]
    public void 品質値の無い項目は最優先として扱う()
    {
        // q を書かない項目は q=1 と同じ。**書いてある項目より弱くしない**
        Assert.Equal("ja", SupportedLanguages.FromAcceptLanguage("ja, en;q=0.9"));
    }

    [Fact]
    public void 品質値0の言語は選ばない()
    {
        // q=0 は「要らない」の意味
        Assert.Equal("ja", SupportedLanguages.FromAcceptLanguage("en;q=0, ja;q=0.1"));
    }

    [Fact]
    public void 地域付きのAcceptLanguageも拾う()
    {
        Assert.Equal("en", SupportedLanguages.FromAcceptLanguage("en-US,en;q=0.9"));
    }

    [Theory]
    [InlineData("fr-FR,de;q=0.8")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    public void 対応する言語が無ければnullになる(string? header)
    {
        Assert.Null(SupportedLanguages.FromAcceptLanguage(header));
    }

    [Fact]
    public void 明示の指定がAcceptLanguageより強い()
    {
        Assert.Equal("en", SupportedLanguages.Resolve("en", "ja,en;q=0.5"));
    }

    [Fact]
    public void 明示が対応外なら無かったものとしてAcceptLanguageを見る()
    {
        Assert.Equal("en", SupportedLanguages.Resolve("fr", "en"));
    }

    [Fact]
    public void どちらも決まらなければ既定の言語になる()
    {
        // **未翻訳のときの落とし先は ja**（LocalizedText.DefaultLanguage）
        Assert.Equal("ja", SupportedLanguages.Resolve(null, null));
        Assert.Equal("ja", SupportedLanguages.Default);
    }

    [Fact]
    public void 既定の言語が選択肢の先頭にある()
    {
        Assert.Equal(SupportedLanguages.Default, SupportedLanguages.All[0]);
    }
}
