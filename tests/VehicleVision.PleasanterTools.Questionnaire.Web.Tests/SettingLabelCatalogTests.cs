using System.Reflection;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>設定画面の項目名と説明が、画面のカタログに揃っていること（Issue #608）。</summary>
/// <remarks>
/// **サーバは設定の鍵だけを返し、項目名と説明は画面のカタログ
/// （<c>settings.label.{鍵}</c> / <c>settings.help.{鍵}</c>）から引く。**
/// 設定を足してカタログへ入れ忘れると、画面に環境変数名がそのまま出る。ここで落とす。
/// </remarks>
public class SettingLabelCatalogTests
{
    /// <summary>項目名と説明を持っているべき言語。**訳した言語を足していき、最後は 7 言語になる。**</summary>
    private static readonly string[] Languages = ["ja", "en", "zh", "ko"];

    private static IReadOnlyList<string> DefinitionKeys() =>
        ((IEnumerable<AppSettingDefinition>)typeof(AppSettingsProvider)
            .GetField("DefinitionList", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!)
        .Select(definition => definition.Key)
        .ToList();

    private static string CatalogPath(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(
            directory.FullName,
            "src",
            "VehicleVision.PleasanterTools.Questionnaire.Frontend",
            "src",
            "admin",
            "lib",
            "i18n",
            "messages",
            language + ".ts");
    }

    [Theory]
    [InlineData("settings.label.")]
    [InlineData("settings.help.")]
    public void すべての設定の項目名と説明が画面のカタログにある(string prefix)
    {
        var missing = new List<string>();
        foreach (var language in Languages)
        {
            var catalog = File.ReadAllText(CatalogPath(language));
            foreach (var key in DefinitionKeys())
            {
                var pattern = @"^\s*['""]" + Regex.Escape(prefix + key) + @"['""]\s*:";
                if (!Regex.IsMatch(catalog, pattern, RegexOptions.Multiline))
                {
                    missing.Add($"{language}: {prefix}{key}");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void カタログに余った設定の項目名は無い()
    {
        var keys = DefinitionKeys().ToHashSet(StringComparer.Ordinal);
        var extra = new List<string>();
        foreach (var language in Languages)
        {
            var catalog = File.ReadAllText(CatalogPath(language));
            foreach (Match match in Regex.Matches(
                catalog, @"^\s*['""]settings\.(?:label|help)\.([A-Z0-9_]+)['""]\s*:", RegexOptions.Multiline))
            {
                if (!keys.Contains(match.Groups[1].Value))
                {
                    extra.Add($"{language}: {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(extra.Count == 0, string.Join(Environment.NewLine, extra));
    }
}
