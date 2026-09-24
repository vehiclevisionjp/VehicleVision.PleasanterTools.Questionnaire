using System.Reflection;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Localization;
using VehicleVision.PleasanterTools.Questionnaire.Web.Localization;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>サーバが返す文言の抜けを見つける。</summary>
/// <remarks>
/// **文言を足したときに翻訳を忘れても、ここで落ちる**
/// （<c>_documents/多言語対応方針.md</c> 4 章）。
/// 落ちなければ、抜けたまま英語の画面に日本語が出る。
/// </remarks>
public class ServerMessagesTests
{
    private static IReadOnlyDictionary<string, LocalizedText> Catalog() =>
        (IReadOnlyDictionary<string, LocalizedText>)typeof(ServerMessages)
            .GetField("Catalog", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static IReadOnlyList<string> DeclaredKeys() =>
        typeof(ServerMessageKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void 宣言した鍵はすべてカタログにある()
    {
        var missing = DeclaredKeys().Except(ServerMessages.Keys).ToArray();

        Assert.True(
            missing.Length == 0,
            "ServerMessageKeys にあってカタログに無い鍵がある。"
            + $"**足し忘れると鍵がそのまま画面に出る**: {string.Join("、", missing)}");
    }

    [Fact]
    public void カタログに余った鍵は無い()
    {
        var extra = ServerMessages.Keys.Except(DeclaredKeys()).ToArray();

        Assert.True(
            extra.Length == 0,
            "カタログにあって ServerMessageKeys に無い鍵がある。"
            + $"**呼べない文言は消し忘れ**: {string.Join("、", extra)}");
    }

    [Fact]
    public void 各言語のカタログに日本語カタログに無い鍵は無い()
    {
        var catalog = Catalog();
        var japaneseKeys = catalog
            .Where(pair => pair.Value.TryGet("ja", out _))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var language in SupportedLanguages.All)
        {
            var extra = catalog
                .Where(pair => pair.Value.TryGet(language, out _))
                .Select(pair => pair.Key)
                .Except(japaneseKeys)
                .ToArray();

            Assert.True(
                extra.Length == 0,
                $"{language} に日本語カタログに無い鍵がある: {string.Join("、", extra)}");
        }
    }

    [Fact]
    public void 各言語にある文言の差し込み番号が日本語と一致する()
    {
        static string[] Placeholders(string value) =>
            Regex.Matches(value, @"\{(\d+)(?:[^}]*)?\}")
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

        foreach (var (key, text) in Catalog())
        {
            Assert.True(text.TryGet("ja", out var japanese), $"ja.{key} が無い");
            var expected = Placeholders(japanese);

            foreach (var language in text.Languages)
            {
                Assert.True(text.TryGet(language, out var translated));
                Assert.True(
                    expected.SequenceEqual(Placeholders(translated), StringComparer.Ordinal),
                    $"{language}.{key} の差し込みが日本語と一致しない");
            }
        }
    }

    [Fact]
    public void 鍵が重複していない()
    {
        var duplicated = DeclaredKeys()
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        // **重複すると後から書いた方でカタログの登録が落ちる**（Dictionary.Add が投げる）
        Assert.True(duplicated.Length == 0, $"鍵が重複している: {string.Join("、", duplicated)}");
    }

    [Fact]
    public void すべての鍵に対応している言語ぶんの文言がある()
    {
        var missing = new List<string>();

        foreach (var key in DeclaredKeys())
        {
            foreach (var language in SupportedLanguages.All)
            {
                var text = ServerMessages.Get(key, language);

                // 鍵そのものが返ってきたら、カタログに無い
                if (text == key || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add($"{key}({language})");
                }
            }
        }

        Assert.True(missing.Count == 0, $"文言が無い: {string.Join("、", missing)}");
    }

    [Fact]
    public void 日本語と英語で別の文字列になっている()
    {
        // **訳し忘れは「同じ文字列が入っている」形で現れる。**
        // 記号だけの文言はまだ無いので、全件が違う値であることを求めてよい
        var untranslated = DeclaredKeys()
            .Where(key => ServerMessages.Get(key, "ja") == ServerMessages.Get(key, "en"))
            .ToArray();

        Assert.True(
            untranslated.Length == 0,
            $"日本語のまま英語のカタログに入っている: {string.Join("、", untranslated)}");
    }

    /// <summary>対応していない言語は英語へ落ちる。</summary>
    /// <remarks>
    /// ⚠️ **対応言語を決め打ちにしない。** 以前は <c>zh</c> を「未翻訳の例」に使っていたが、
    /// **訳が入った時点でこの試験は落ちる。** 確かめたいのは
    /// 「カタログに無い言語は英語になる」ことなので、対応外の言語で確かめる。
    /// </remarks>
    [Fact]
    public void 対応外の言語は英語へ落ちる()
    {
        Assert.Equal(
            ServerMessages.Get(ServerMessageKeys.InvalidCredentials, "en"),
            ServerMessages.Get(ServerMessageKeys.InvalidCredentials, "fr"));
    }

    /// <summary>どの対応言語を指定しても空にならない。</summary>
    /// <remarks>
    /// **訳が無い言語は英語、それも無ければ日本語へ落ちる**ので、空になる道は無い。
    /// ⚠️ **空を返すと画面に何も出ない。** 落とし先が効いているかをここで押さえる。
    /// </remarks>
    [Fact]
    public void どの対応言語でも空にならない()
    {
        foreach (var language in SupportedLanguages.All)
        {
            foreach (var key in ServerMessages.Keys)
            {
                Assert.False(
                    string.IsNullOrEmpty(ServerMessages.Get(key, language)),
                    $"{language} / {key}");
            }
        }
    }

    [Fact]
    public void 知らない鍵でも落ちない()
    {
        // **例外にしない。** 文言の抜けは上のテストで落ちるので、
        // 実行時にまで持ち込む必要が無い
        Assert.Equal("nope.nothing", ServerMessages.Get("nope.nothing", "ja"));
    }

    [Fact]
    public void 差し込みのある文言は値が入る()
    {
        var ja = ServerMessages.Get(ServerMessageKeys.PasswordTooShort, "ja", 12);
        var en = ServerMessages.Get(ServerMessageKeys.PasswordTooShort, "en", 12);

        Assert.Contains("12", ja, StringComparison.Ordinal);
        Assert.Contains("12", en, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", ja, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", en, StringComparison.Ordinal);
    }
}
