using System.Globalization;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>Pleasanter へ渡す日時と、返ってきた日時を変換する。</summary>
/// <remarks>
/// <para>
/// **Pleasanter は日時をサーバの OS ローカル時刻で DB へ保存し、API の境界で
/// 「API キー保有ユーザの <c>TimeZone</c>」との間で変換する**
/// （<c>_documents/実機検証結果.md</c> 4 章。実測で確定）。
/// </para>
/// <para>
/// **変換はここ 1 か所に閉じ込める。** アプリ内部は <see cref="DateTimeOffset"/> で持ち、
/// Pleasanter へ渡す直前にだけこの型を通す。
/// </para>
/// </remarks>
public sealed class PleasanterDateTime
{
    /// <summary>未設定の日付列が返してくる値。</summary>
    /// <remarks>
    /// **「未回答」と「1899-12-30 と回答」を API 応答だけでは区別できない**
    /// （<c>_documents/実機検証結果.md</c> 5 章）。
    /// </remarks>
    public static readonly DateTime UnsetDate = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Pleasanter がやり取りする日時の書式。</summary>
    private const string Format = "yyyy-MM-ddTHH:mm:ss";

    private readonly TimeZoneInfo _apiKeyUserTimeZone;

    public PleasanterDateTime(string apiKeyUserTimeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyUserTimeZoneId);

        // IANA（Asia/Tokyo）と Windows（Tokyo Standard Time）のどちらでも受ける
        _apiKeyUserTimeZone = TimeZoneInfo.FindSystemTimeZoneById(apiKeyUserTimeZoneId);
    }

    /// <summary>Pleasanter へ渡す文字列にする。</summary>
    public string ToPleasanter(DateTimeOffset value) =>
        TimeZoneInfo
            .ConvertTime(value, _apiKeyUserTimeZone)
            .ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Pleasanter から返ってきた文字列を読む。</summary>
    /// <returns>
    /// 未設定（<see cref="UnsetDate"/>）または解釈できない場合は <c>null</c>。
    /// </returns>
    public DateTimeOffset? FromPleasanter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedAsUtc))
        {
            return null;
        }

        // オフセット付きで返ってきた場合はそのまま使う
        if (value.EndsWith('Z') || value.Contains('+') || value.LastIndexOf('-') > 7)
        {
            return parsedAsUtc;
        }

        // オフセットが無い場合は API キー保有ユーザのタイムゾーンとして解釈する
        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return null;
        }

        if (local == UnsetDate)
        {
            return null;
        }

        var offset = _apiKeyUserTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>日付だけの文字列（<c>2026-03-01</c>）を Pleasanter へ渡す形にする。</summary>
    /// <remarks>その日の 0 時として、API キー保有ユーザのタイムゾーンで解釈される。</remarks>
    public static string? DateOnlyToPleasanter(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var date)
            ? date.ToDateTime(TimeOnly.MinValue).ToString(Format, CultureInfo.InvariantCulture)
            : null;
}
