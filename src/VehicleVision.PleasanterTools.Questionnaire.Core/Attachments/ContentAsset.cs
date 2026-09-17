namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>説明文と完了画面で配る資産の共通条件。</summary>
public static class ContentAsset
{
    /// <summary>1 件あたりの既定上限（バイト）。</summary>
    /// <remarks>
    /// **PDF や Office 文書を配る用途**なのでヘッダ画像より大きくする。
    /// ただし既定の DB 保存では Base64 になり、公開のたびにスナップショットも増えるため、
    /// 回答添付の合計上限より小さい 10 MB に留める。
    /// </remarks>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>アンケート 1 件へ保存できる配布資産の既定上限。</summary>
    public const int DefaultMaxAssetsPerSurvey = 20;

    /// <summary>画像として埋め込める型か。</summary>
    public static bool IsImage(string? contentType) =>
        contentType is not null
        && HeaderImage.IsAllowedContentType(contentType);
}
