using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>運用側が指定する回答画面の配色固定。</summary>
public sealed class ColorSchemeOptions
{
    public const string ColorSchemeKey = "QUESTIONNAIRE_COLOR_SCHEME";

    public ThemeColorScheme ColorScheme { get; init; } = ThemeColorScheme.System;

    public static ColorSchemeOptions FromSnapshot(AppSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ColorSchemeOptions
        {
            ColorScheme = Enum.Parse<ThemeColorScheme>(snapshot[ColorSchemeKey]),
        };
    }

    public static string Normalize(string value)
    {
        if (!Enum.TryParse<ThemeColorScheme>(value, ignoreCase: true, out var colorScheme)
            || !Enum.IsDefined(colorScheme))
        {
            throw new AppSettingValidationException(
                "回答画面の配色固定は System、Light、Dark のいずれかで指定してください。");
        }

        return colorScheme.ToString();
    }
}
