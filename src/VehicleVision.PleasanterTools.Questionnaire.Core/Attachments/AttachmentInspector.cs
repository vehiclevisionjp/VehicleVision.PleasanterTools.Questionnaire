using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>添付ファイル 1 件。</summary>
/// <param name="FileName">回答者が付けたファイル名。</param>
/// <param name="Content">中身。</param>
public sealed record IncomingAttachment(string FileName, ReadOnlyMemory<byte> Content);

/// <summary>検査の結果。</summary>
/// <param name="FileName">対象のファイル名。全体に対するエラーでは <c>null</c>。</param>
/// <param name="Reason">受け付けなかった理由。</param>
public sealed record AttachmentRejection(string? FileName, AttachmentRejectionReason Reason);

/// <summary>添付ファイルを 3 層で検査する。</summary>
/// <remarks>
/// 1. 拡張子の許可リスト 2. 先頭バイトと拡張子の一致 3. ウイルススキャン（任意）。
/// **必ず送信待ちへ保存する前に呼ぶこと。** 保存してからでは未検査のバイナリが DB に載る
/// （<c>_documents/添付ファイル検査-運用手順書.md</c>）。
/// </remarks>
public sealed class AttachmentInspector(AttachmentPolicy policy, IVirusScanner? scanner = null)
{
    /// <summary>先頭バイトの照合に必要な長さ。</summary>
    private const int SignatureProbeLength = 16;

    public async Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IReadOnlyList<IncomingAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var rejections = ImmutableArray.CreateBuilder<AttachmentRejection>();

        if (attachments.Count > policy.MaxFileCount)
        {
            rejections.Add(new AttachmentRejection(null, AttachmentRejectionReason.TooMany));
        }

        foreach (var attachment in attachments)
        {
            var reason = InspectStatically(attachment);
            if (reason is not null)
            {
                rejections.Add(new AttachmentRejection(attachment.FileName, reason.Value));
                continue;
            }

            if (!policy.VirusScanEnabled)
            {
                continue;
            }

            if (scanner is null)
            {
                // 有効にしたのにスキャナが無い。**通さない**
                rejections.Add(new AttachmentRejection(
                    attachment.FileName, AttachmentRejectionReason.ScannerUnavailable));
                continue;
            }

            try
            {
                var verdict = await scanner.ScanAsync(attachment.Content, cancellationToken)
                    .ConfigureAwait(false);
                if (verdict is ScanVerdict.Infected)
                {
                    rejections.Add(new AttachmentRejection(
                        attachment.FileName, AttachmentRejectionReason.Infected));
                }
            }
            catch (VirusScannerUnavailableException)
            {
                rejections.Add(new AttachmentRejection(
                    attachment.FileName, AttachmentRejectionReason.ScannerUnavailable));
            }
        }

        return rejections.ToImmutable();
    }

    /// <summary>スキャナを使わない検査（1 層目と 2 層目）。</summary>
    private AttachmentRejectionReason? InspectStatically(IncomingAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.FileName)
            || attachment.FileName.Contains('/')
            || attachment.FileName.Contains('\\')
            || attachment.FileName.Contains('\0'))
        {
            return AttachmentRejectionReason.InvalidFileName;
        }

        if (attachment.Content.Length > policy.MaxFileSizeBytes)
        {
            return AttachmentRejectionReason.TooLarge;
        }

        var extension = Path.GetExtension(attachment.FileName);
        if (string.IsNullOrEmpty(extension)
            || !policy.AllowedExtensions.Contains(AttachmentPolicy.NormalizeExtension(extension)))
        {
            return AttachmentRejectionReason.ExtensionNotAllowed;
        }

        var probeLength = Math.Min(SignatureProbeLength, attachment.Content.Length);
        if (!FileSignature.Matches(extension, attachment.Content.Span[..probeLength]))
        {
            return AttachmentRejectionReason.ContentDoesNotMatchExtension;
        }

        return null;
    }
}
