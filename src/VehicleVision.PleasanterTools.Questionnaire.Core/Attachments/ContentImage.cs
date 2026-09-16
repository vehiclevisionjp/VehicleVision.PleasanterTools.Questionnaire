using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>説明文と完了画面で使う画像資産の受け入れ条件。</summary>
public static class ContentImage
{
    /// <summary>1 枚あたりの上限（バイト）。</summary>
    /// <remarks>
    /// **本文には複数枚置ける**ため、常に 1 枚だけのヘッダ画像より小さくする。
    /// </remarks>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>アンケート 1 件へ保存できる本文用資産の上限。</summary>
    public const int MaxAssetsPerSurvey = 20;

    /// <summary>要求本文の上限（バイト）。**multipart の分を含める。**</summary>
    public const long MaxRequestBodyBytes = MaxBytes + (256 * 1024);

    /// <summary>検査に使う条件。**画像 1 枚だけ。**</summary>
    public static AttachmentPolicy Policy { get; } = AttachmentPolicy.Create(
        HeaderImage.AllowedExtensions,
        MaxBytes,
        maxFileCount: 1,
        virusScanEnabled: false,
        maxTotalBytes: MaxBytes);

    /// <summary>1 枚を検査する。**通れば空。**</summary>
    public static Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IncomingAttachment image,
        CancellationToken cancellationToken = default) =>
        new AttachmentInspector(Policy).InspectAsync([image], cancellationToken);
}
