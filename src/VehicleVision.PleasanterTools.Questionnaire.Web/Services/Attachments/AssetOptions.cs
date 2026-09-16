using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services.Attachments;

/// <summary>説明文と完了画面で配る資産の受け入れ設定。</summary>
/// <remarks>
/// **回答添付とは別の許可リストを持つ。** 配布用途で広げた形式を、
/// 回答者から受け取る入口まで広げないため。
/// </remarks>
public sealed class AssetOptions
{
    public static readonly ImmutableArray<string> DefaultAllowedExtensions =
    [
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
    ];

    private static readonly FrozenDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".zip"] = "application/zip",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ForbiddenExtensions =
        new[] { ".html", ".htm", ".svg", ".exe", ".bat", ".cmd", ".ps1" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public ImmutableArray<string> AllowedExtensions { get; init; } = DefaultAllowedExtensions;

    public long MaxFileSizeBytes { get; init; } = ContentAsset.DefaultMaxBytes;

    public int MaxFileCount { get; init; } = ContentAsset.DefaultMaxAssetsPerSurvey;

    public bool VirusScanEnabled { get; init; }

    public long MaxRequestBodyBytes => MaxFileSizeBytes + (256 * 1024);

    public AttachmentPolicy ToPolicy() => AttachmentPolicy.Create(
        AllowedExtensions,
        MaxFileSizeBytes,
        maxFileCount: 1,
        VirusScanEnabled,
        maxTotalBytes: MaxFileSizeBytes);

    /// <summary>検査済みのファイル名から配信型を決める。</summary>
    /// <remarks>ブラウザが申告した Content-Type は使わない。</remarks>
    public string? ContentTypeOf(string fileName) =>
        ContentTypes.GetValueOrDefault(
            AttachmentPolicy.NormalizeExtension(Path.GetExtension(fileName)));

    public bool IsAllowed(string fileName, string? contentType)
    {
        var extension = AttachmentPolicy.NormalizeExtension(Path.GetExtension(fileName));
        return contentType is not null
            && AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            && string.Equals(
                ContentTypes.GetValueOrDefault(extension), contentType, StringComparison.Ordinal);
    }

    public static AssetOptions FromConfiguration(
        IConfiguration configuration,
        bool virusScanEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new AssetOptions();
        var extensions = ReadExtensions(configuration) ?? defaults.AllowedExtensions;
        foreach (var extension in extensions)
        {
            if (ForbiddenExtensions.Contains(extension) || !ContentTypes.ContainsKey(extension))
            {
                throw Invalid("QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS", extension);
            }
        }

        return new AssetOptions
        {
            AllowedExtensions = extensions,
            MaxFileSizeBytes = ReadInt64(
                configuration, "QUESTIONNAIRE_ASSET_MAXFILESIZEBYTES")
                ?? defaults.MaxFileSizeBytes,
            MaxFileCount = ReadInt32(configuration, "QUESTIONNAIRE_ASSET_MAXFILECOUNT")
                ?? defaults.MaxFileCount,
            VirusScanEnabled = virusScanEnabled,
        };
    }

    private static ImmutableArray<string>? ReadExtensions(IConfiguration configuration)
    {
        var raw = configuration["QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var extensions = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AttachmentPolicy.NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return extensions.IsEmpty
            ? throw Invalid("QUESTIONNAIRE_ASSET_ALLOWEDEXTENSIONS", raw)
            : extensions;
    }

    private static int? ReadInt32(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : throw Invalid(key, raw);
    }

    private static long? ReadInt64(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : throw Invalid(key, raw);
    }

    private static InvalidOperationException Invalid(string key, string raw) =>
        new($"{key} の値を解釈できない: {raw}");
}

/// <summary>配布資産を回答添付と同じ検査器へ通す。</summary>
public sealed class AssetInspector(AssetOptions options, IVirusScanner? scanner = null)
{
    private const int MaximumFileNameLength = 256;
    private readonly AttachmentInspector _inner = new(options.ToPolicy(), scanner);

    public Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IncomingAttachment asset,
        CancellationToken cancellationToken = default)
    {
        // **保存列と配信ヘッダに安全に収まる名前だけを受ける。**
        // 長すぎる名前を DB 例外や不正な Content-Disposition へ進ませない
        if (asset.FileName.Length > MaximumFileNameLength
            || asset.FileName.Any(char.IsControl))
        {
            return Task.FromResult(ImmutableArray.Create(
                new AttachmentRejection(
                    asset.FileName, AttachmentRejectionReason.InvalidFileName)));
        }

        return _inner.InspectAsync([asset], cancellationToken);
    }
}
