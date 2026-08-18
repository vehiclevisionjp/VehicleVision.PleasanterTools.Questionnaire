using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>添付ファイルの受け入れ条件。</summary>
public sealed class AttachmentPolicy
{
    /// <summary>許可する拡張子。**禁止リストではなく許可リスト。**</summary>
    /// <remarks>危険な拡張子を挙げていく方式は必ず漏れる。</remarks>
    public required FrozenSet<string> AllowedExtensions { get; init; }

    /// <summary>1 件あたりのサイズ上限（バイト）。</summary>
    public required long MaxFileSizeBytes { get; init; }

    /// <summary>1 設問あたりの個数上限。</summary>
    public required int MaxFileCount { get; init; }

    /// <summary>ウイルススキャンを行うか。</summary>
    public bool VirusScanEnabled { get; init; }

    public static AttachmentPolicy Create(
        IEnumerable<string> allowedExtensions,
        long maxFileSizeBytes,
        int maxFileCount,
        bool virusScanEnabled = false) => new()
        {
            AllowedExtensions = allowedExtensions
                .Select(NormalizeExtension)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxFileCount = maxFileCount,
            VirusScanEnabled = virusScanEnabled,
        };

    /// <summary>先頭のドットを補い、小文字へ揃える。</summary>
    public static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}
