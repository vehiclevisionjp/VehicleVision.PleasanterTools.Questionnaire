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

    /// <summary>1 回の送信で受け取る合計サイズの上限（バイト）。</summary>
    /// <remarks>
    /// **1 件あたりの上限だけでは足りない。** 上限いっぱいの添付を設問の数だけ並べられると、
    /// 送信待ちの行がその分だけ膨らむ（添付は <c>PayloadJson</c> へ Base64 で載る。
    /// <c>_documents/アプリケーション設計.md</c> 7 章）。
    /// **Base64 は元のバイト数の約 4/3 になる。** 上限は元のバイト数で見ているので、
    /// DB の容量を見積もるときはその分を足すこと。
    /// </remarks>
    public required long MaxTotalBytes { get; init; }

    /// <summary>ウイルススキャンを行うか。</summary>
    public bool VirusScanEnabled { get; init; }

    public static AttachmentPolicy Create(
        IEnumerable<string> allowedExtensions,
        long maxFileSizeBytes,
        int maxFileCount,
        bool virusScanEnabled = false,
        long? maxTotalBytes = null) => new()
        {
            AllowedExtensions = allowedExtensions
                .Select(NormalizeExtension)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            MaxFileSizeBytes = maxFileSizeBytes,
            MaxFileCount = maxFileCount,
            // 既定は「1 件あたりの上限 × 個数上限」。**掛け算で桁あふれさせない**
            MaxTotalBytes = maxTotalBytes ?? Multiply(maxFileSizeBytes, maxFileCount),
            VirusScanEnabled = virusScanEnabled,
        };

    /// <summary>設問ごとの上限で絞り込む。**この設定より緩くはしない。**</summary>
    /// <remarks>
    /// 設問側の上限（<c>QuestionSettings</c>）はアンケートを作る人が決める値なので、
    /// **そこへ大きな数を書いても運用者が決めた全体の上限は超えられないようにする。**
    /// </remarks>
    public AttachmentPolicy Tighten(int? maxFileCount, long? maxFileSizeBytes) => new()
    {
        AllowedExtensions = AllowedExtensions,
        MaxFileSizeBytes = Math.Min(MaxFileSizeBytes, maxFileSizeBytes ?? MaxFileSizeBytes),
        MaxFileCount = Math.Min(MaxFileCount, maxFileCount ?? MaxFileCount),
        MaxTotalBytes = MaxTotalBytes,
        VirusScanEnabled = VirusScanEnabled,
    };

    private static long Multiply(long size, int count)
    {
        try
        {
            return checked(size * count);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>先頭のドットを補い、小文字へ揃える。</summary>
    public static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}
