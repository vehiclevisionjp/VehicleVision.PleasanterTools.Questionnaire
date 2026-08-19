using System.Reflection;
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

    [Fact]
    public void 対応していない言語は既定の言語へ落ちる()
    {
        Assert.Equal(
            ServerMessages.Get(ServerMessageKeys.InvalidCredentials, SupportedLanguages.Default),
            ServerMessages.Get(ServerMessageKeys.InvalidCredentials, "fr"));
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
