using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>添付ファイル 1 件。</summary>
/// <param name="FileName">回答者が付けたファイル名。</param>
/// <param name="Content">中身。</param>
public sealed record IncomingAttachment(string FileName, ReadOnlyMemory<byte> Content);

/// <summary>設問に紐づく添付 1 件。</summary>
/// <param name="QuestionId">どの設問への添付か。</param>
/// <param name="File">添付そのもの。</param>
public sealed record AnsweredAttachment(string QuestionId, IncomingAttachment File);

/// <summary>検査の結果。</summary>
/// <param name="FileName">対象のファイル名。全体に対するエラーでは <c>null</c>。</param>
/// <param name="Reason">受け付けなかった理由。</param>
/// <param name="QuestionId">どの設問の添付か。設問に紐づかないエラーでは <c>null</c>。</param>
public sealed record AttachmentRejection(
    string? FileName,
    AttachmentRejectionReason Reason,
    string? QuestionId = null);

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

    /// <summary>設問 1 つ分の添付を検査する。</summary>
    public Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IReadOnlyList<IncomingAttachment> attachments,
        CancellationToken cancellationToken = default) =>
        InspectAsync(attachments, policy, questionId: null, cancellationToken);

    /// <summary>1 回の送信ぶんをまとめて検査する。</summary>
    /// <remarks>
    /// **設問ごとの上限と、送信全体の合計の両方を見る。**
    /// 1 件あたりの上限しか見ないと、上限いっぱいの添付を設問の数だけ並べられる。
    /// </remarks>
    /// <param name="attachments">設問に紐づいた添付。</param>
    /// <param name="policyFor">
    /// 設問ごとの上限を返す。<c>null</c> なら共通の上限だけで見る。
    /// </param>
    public async Task<ImmutableArray<AttachmentRejection>> InspectSubmissionAsync(
        IReadOnlyList<AnsweredAttachment> attachments,
        Func<string, AttachmentPolicy>? policyFor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var total = attachments.Sum(attachment => (long)attachment.File.Content.Length);
        if (total > policy.MaxTotalBytes)
        {
            // **超えていると分かっているものをスキャナへ流さない。** 無駄に待たせるだけ
            return [new AttachmentRejection(null, AttachmentRejectionReason.TotalTooLarge)];
        }

        var rejections = ImmutableArray.CreateBuilder<AttachmentRejection>();

        foreach (var group in attachments.GroupBy(
            attachment => attachment.QuestionId, StringComparer.Ordinal))
        {
            var files = group.Select(attachment => attachment.File).ToArray();
            var groupPolicy = policyFor?.Invoke(group.Key) ?? policy;

            rejections.AddRange(
                await InspectAsync(files, groupPolicy, group.Key, cancellationToken)
                    .ConfigureAwait(false));
        }

        return rejections.ToImmutable();
    }

    private async Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IReadOnlyList<IncomingAttachment> attachments,
        AttachmentPolicy effective,
        string? questionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachments);

        var rejections = ImmutableArray.CreateBuilder<AttachmentRejection>();

        if (attachments.Count > effective.MaxFileCount)
        {
            rejections.Add(new AttachmentRejection(
                null, AttachmentRejectionReason.TooMany, questionId));
        }

        foreach (var attachment in attachments)
        {
            var reason = InspectStatically(attachment, effective);
            if (reason is not null)
            {
                rejections.Add(new AttachmentRejection(
                    attachment.FileName, reason.Value, questionId));
                continue;
            }

            if (!effective.VirusScanEnabled)
            {
                continue;
            }

            if (scanner is null)
            {
                // 有効にしたのにスキャナが無い。**通さない**
                rejections.Add(new AttachmentRejection(
                    attachment.FileName, AttachmentRejectionReason.ScannerUnavailable, questionId));
                continue;
            }

            try
            {
                var verdict = await scanner.ScanAsync(attachment.Content, cancellationToken)
                    .ConfigureAwait(false);
                if (verdict is ScanVerdict.Infected)
                {
                    rejections.Add(new AttachmentRejection(
                        attachment.FileName, AttachmentRejectionReason.Infected, questionId));
                }
            }
            catch (VirusScannerUnavailableException)
            {
                rejections.Add(new AttachmentRejection(
                    attachment.FileName, AttachmentRejectionReason.ScannerUnavailable, questionId));
            }
        }

        return rejections.ToImmutable();
    }

    /// <summary>スキャナを使わない検査（1 層目と 2 層目）。</summary>
    private static AttachmentRejectionReason? InspectStatically(
        IncomingAttachment attachment,
        AttachmentPolicy policy)
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
