namespace VehicleVision.PleasanterTools.Questionnaire.Web;

/// <summary>OpenAPI 文書を公開するかを表す。</summary>
public sealed record OpenApiExposureOptions(bool Enabled)
{
    /// <summary>OpenAPI 文書を公開する設定。</summary>
    public const string EnabledSetting = "QUESTIONNAIRE_OPENAPI_ENABLED";

    /// <summary>環境変数の明示値を読み取る。</summary>
    public static OpenApiExposureOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[EnabledSetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new OpenApiExposureOptions(false);
        }

        return bool.TryParse(raw.Trim(), out var enabled)
            ? new OpenApiExposureOptions(enabled)
            : throw new InvalidOperationException(
                $"{EnabledSetting} must be true or false.");
    }
}
