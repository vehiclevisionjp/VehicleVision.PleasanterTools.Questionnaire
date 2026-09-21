using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>回答通知メールの集約設定（Issue #357）。</summary>
public sealed class ResponseNotificationMailerOptions
{
    /// <summary>集約間隔の設定名。分単位で指定する。</summary>
    public const string DigestIntervalMinutesKey =
        "QUESTIONNAIRE_RESPONSE_NOTIFICATION_DIGEST_MINUTES";

    /// <summary>集約間隔の既定値。</summary>
    public static readonly TimeSpan DefaultDigestInterval = TimeSpan.FromDays(1);

    /// <summary>回答ごとのメールへ近づけないための最短集約間隔。</summary>
    public static readonly TimeSpan MinimumDigestInterval = TimeSpan.FromHours(1);

    /// <summary>アンケートごとにメールをまとめる間隔。</summary>
    public TimeSpan DigestInterval { get; init; } = DefaultDigestInterval;

    /// <summary>設定から読む。</summary>
    /// <remarks>
    /// **1 時間未満は起動時に断る。** 丸めて気付かず短い設定のまま運用する状態を避ける。
    /// </remarks>
    public static ResponseNotificationMailerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[DigestIntervalMinutesKey];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResponseNotificationMailerOptions();
        }

        if (!int.TryParse(raw, CultureInfo.InvariantCulture, out var minutes))
        {
            throw new InvalidOperationException(
                $"{DigestIntervalMinutesKey} は整数の分数で指定する（今の値: {raw}）。");
        }

        if (minutes < MinimumDigestInterval.TotalMinutes)
        {
            throw new InvalidOperationException(
                $"{DigestIntervalMinutesKey} は {MinimumDigestInterval.TotalMinutes} 分以上で指定する"
                + $"（今の値: {minutes}）。");
        }

        return new ResponseNotificationMailerOptions
        {
            DigestInterval = TimeSpan.FromMinutes(minutes),
        };
    }
}
