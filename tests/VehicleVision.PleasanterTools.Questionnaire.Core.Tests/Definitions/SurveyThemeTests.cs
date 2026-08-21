using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Tests.Definitions;

/// <summary>回答画面のテーマ（Issue #56）。</summary>
/// <remarks>
/// **色の検査がここの主題。** 利用者が入れた文字列は
/// そのまま CSS のカスタムプロパティへ入るので、
/// **形を絞り切れているかを「通るもの」と「通らないもの」の両方で確かめる。**
/// </remarks>
public class SurveyThemeTests
{
    [Theory]
    [InlineData("#fff")]
    [InlineData("#FFF")]
    [InlineData("#175cd3")]
    [InlineData("#175CD3")]
    [InlineData("#000000")]
    public void 六桁と三桁の十六進は通る(string value)
    {
        Assert.True(ThemeColor.IsValid(value));
    }

    [Theory]
    // 色の名前と関数は受け付けない（形を絞るため）
    [InlineData("red")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("var(--accent)")]
    // 井桁が無い / 桁数が違う
    [InlineData("175cd3")]
    [InlineData("#12")]
    [InlineData("#1234")]
    [InlineData("#1234567")]
    // 十六進でない文字
    [InlineData("#gggggg")]
    // **画面を乗っ取るための値。** 1 つでも通ればスタイル表を書き換えられる
    [InlineData("#fff; } body { display: none } .x {")]
    [InlineData("#fff</style><script>alert(1)</script>")]
    [InlineData("#fff url(https://example.com/x)")]
    // 空白混じり（前後の空白も切り詰めずに落とす）
    [InlineData(" #ffffff")]
    [InlineData("#ffffff ")]
    [InlineData("")]
    [InlineData(null)]
    public void 形の違う値は通さない(string? value)
    {
        Assert.False(ThemeColor.IsValid(value));
        Assert.Null(ThemeColor.Normalize(value));
    }

    [Fact]
    public void 大文字は小文字へ揃える()
    {
        Assert.Equal("#175cd3", ThemeColor.Normalize("#175CD3"));
    }

    [Fact]
    public void 何も指定していなければ既定として扱う()
    {
        Assert.True(new SurveyTheme().IsDefault);
    }

    [Fact]
    public void 色を一つでも指定すれば既定ではない()
    {
        Assert.False(new SurveyTheme { AccentColor = "#175cd3" }.IsDefault);
    }

    [Fact]
    public void 形の違う色は項目名で報告する()
    {
        var theme = new SurveyTheme
        {
            AccentColor = "#175cd3",
            BackgroundColor = "red",
            TextColor = "#fff; }",
        };

        var invalid = theme.InvalidColors();

        Assert.Equal(2, invalid.Length);
        Assert.Contains(nameof(SurveyTheme.BackgroundColor), invalid);
        Assert.Contains(nameof(SurveyTheme.TextColor), invalid);
    }

    [Fact]
    public void 後から足した色も検査する()
    {
        // **項目を足したときに検査へ足し忘れると、CSS へ素通りする**（Issue #109）
        var theme = new SurveyTheme
        {
            AccentTextColor = "red",
            SurfaceColor = "rgb(1,2,3)",
            BorderColor = "#fff; }",
        };

        var invalid = theme.InvalidColors();

        Assert.Equal(3, invalid.Length);
        Assert.Contains(nameof(SurveyTheme.AccentTextColor), invalid);
        Assert.Contains(nameof(SurveyTheme.SurfaceColor), invalid);
        Assert.Contains(nameof(SurveyTheme.BorderColor), invalid);
    }

    [Fact]
    public void 後から足した色も捨てて既定へ落とす()
    {
        var theme = new SurveyTheme
        {
            AccentTextColor = "#FFF",
            SurfaceColor = "red",
            BorderColor = "#fff; } body { display: none }",
        }.Sanitized();

        Assert.Equal("#fff", theme.AccentTextColor);
        Assert.Null(theme.SurfaceColor);
        Assert.Null(theme.BorderColor);
    }

    [Fact]
    public void 配色だけを選んでも既定ではない()
    {
        Assert.False(new SurveyTheme { Preset = ThemePreset.Midnight }.IsDefault);
    }

    [Fact]
    public void 列挙の範囲外の配色は指定なしへ落とす()
    {
        // JSON から数値で入ってくると、列挙にない値になり得る
        var theme = new SurveyTheme { Preset = (ThemePreset)999 }.Sanitized();

        Assert.Equal(ThemePreset.None, theme.Preset);
    }

    [Fact]
    public void 配色は文字列で書く()
    {
        // **数値だと、列挙に値を挿入したときに過去の版の意味が変わる**
        var json = SurveyJson.Serialize(
            Definition(new SurveyTheme { Preset = ThemePreset.Forest }));

        Assert.Contains("\"Forest\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 未指定は不備として挙げない()
    {
        Assert.Empty(new SurveyTheme().InvalidColors());
    }

    [Fact]
    public void 形の違う色は捨てて既定へ落とす()
    {
        var theme = new SurveyTheme
        {
            AccentColor = "#175CD3",
            BackgroundColor = "red",
            TextColor = "#fff; } body { display: none }",
        }.Sanitized();

        Assert.Equal("#175cd3", theme.AccentColor);
        Assert.Null(theme.BackgroundColor);
        Assert.Null(theme.TextColor);
    }

    [Fact]
    public void 列挙の範囲外の書体は既定へ落とす()
    {
        // JSON から数値で入ってくると、列挙にない値になり得る
        var theme = new SurveyTheme { Font = (ThemeFont)999 }.Sanitized();

        Assert.Equal(ThemeFont.System, theme.Font);
    }

    [Fact]
    public void 識別子として読めないヘッダ画像は捨てる()
    {
        var theme = new SurveyTheme { HeaderImageId = "../../etc/passwd" }.Sanitized();

        Assert.Null(theme.HeaderImageId);
        Assert.Null(theme.HeaderImage());
    }

    [Fact]
    public void ヘッダ画像は書き方を揃えて返す()
    {
        var assetId = Guid.NewGuid();
        var theme = new SurveyTheme { HeaderImageId = assetId.ToString("B") }.Sanitized();

        Assert.Equal(assetId.ToString(), theme.HeaderImageId);
        Assert.Equal(assetId, theme.HeaderImage());
    }

    [Fact]
    public void テーマを持たない定義のJSONにテーマは載らない()
    {
        // **既定の見た目を壊さない**（Issue #56）。触っていない定義の JSON を変えない
        var json = SurveyJson.Serialize(Definition(theme: null));

        Assert.DoesNotContain("theme", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void テーマは書いて読み直せる()
    {
        var assetId = Guid.NewGuid();
        var original = new SurveyTheme
        {
            AccentColor = "#175cd3",
            BackgroundColor = "#ffffff",
            TextColor = "#101828",
            Font = ThemeFont.Serif,
            HeaderImageId = assetId.ToString(),
        };

        var json = SurveyJson.Serialize(Definition(original));
        var restored = SurveyJson.Deserialize<SurveyDefinition>(json)?.Theme;

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void 書体は文字列で書く()
    {
        // **数値だと、列挙に値を挿入したときに過去の版の意味が変わる**
        var json = SurveyJson.Serialize(Definition(new SurveyTheme { Font = ThemeFont.Rounded }));

        Assert.Contains("\"Rounded\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void テーマの無い過去の版は既定の見た目として読める()
    {
        // **公開済みの版には移行が要らない**（M0008_SurveyTheme）
        const string json = """
            {
              "surveyId": "s1",
              "version": 1,
              "title": { "ja": "満足度調査" },
              "pages": []
            }
            """;

        var definition = SurveyJson.Deserialize<SurveyDefinition>(json);

        Assert.NotNull(definition);
        Assert.Null(definition.Theme);
    }

    [Fact]
    public void 複製はテーマも写す()
    {
        // **`with` で丸ごと写しているので、項目を足しても写し漏れない**
        var theme = new SurveyTheme { AccentColor = "#175cd3", Font = ThemeFont.Sans };

        var copied = SurveyDuplication.Copy(Definition(theme), Guid.NewGuid().ToString());

        Assert.Equal(theme, copied.Theme);
    }

    private static SurveyDefinition Definition(SurveyTheme? theme) => new()
    {
        SurveyId = "s1",
        Version = 1,
        Title = LocalizedText.Japanese("満足度調査"),
        Theme = theme,
    };
}
