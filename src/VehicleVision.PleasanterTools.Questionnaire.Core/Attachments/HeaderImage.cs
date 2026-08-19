using System.Collections.Frozen;
using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>ヘッダ画像の受け入れ条件（Issue #56）。</summary>
/// <remarks>
/// <para>
/// **添付ファイルと同じ道を通す**（<see cref="AttachmentInspector"/>）。
/// 拡張子の許可リスト・先頭バイトとの一致・大きさの上限を、
/// 画像のためにもう一度書き起こさない。**別に書くと、片方だけ抜けが直る。**
/// </para>
/// <para>
/// **管理者が上げるものだが、検査を緩めない。** 管理画面の口を通れる相手なら
/// 何を置いてもよいことにすると、乗っ取られた 1 つの管理者の口が、
/// **公開アンケートを見た全員へ好きなファイルを配る口**になる。
/// </para>
/// <para>
/// ⚠️ **SVG は許可しない。** 中に script を書けるうえ、先頭バイトでの見分けが効かない
/// （XML なので何でも先頭に置ける）。
/// </para>
/// </remarks>
public static class HeaderImage
{
    /// <summary>許可する拡張子。**先頭バイトで照合できる画像だけ。**</summary>
    /// <remarks>
    /// <see cref="FileSignature"/> に載っている形式に限ってある。
    /// **照合できない形式を足すと、2 層目が素通りになる。**
    /// </remarks>
    public static readonly ImmutableArray<string> AllowedExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>1 枚あたりの上限（バイト）。</summary>
    /// <remarks>
    /// **回答画面を開いた全員が毎回受け取る。** 添付の上限（5 MB）より厳しくしてある。
    /// 通信の細い場所から答える回答者を待たせないため。
    /// </remarks>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>要求本文の上限（バイト）。**既定値に任せない。**</summary>
    public const long MaxRequestBodyBytes = MaxBytes + (256 * 1024);

    /// <summary>拡張子から配信時の型を決める。</summary>
    /// <remarks>
    /// ⚠️ **ブラウザが名乗った <c>Content-Type</c> を信じない。**
    /// 名乗りをそのまま覚えて配ると、<c>text/html</c> を名乗った「画像」を
    /// 自分のドメインから配ることになる。
    /// **検査を通った拡張子から、こちらで決めた型だけを使う。**
    /// </remarks>
    private static readonly FrozenDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>検査に使う条件。**1 枚だけ。**</summary>
    public static AttachmentPolicy Policy { get; } = AttachmentPolicy.Create(
        AllowedExtensions,
        MaxBytes,
        maxFileCount: 1,
        virusScanEnabled: false,
        maxTotalBytes: MaxBytes);

    /// <summary>ファイル名から配信時の型を返す。**分からなければ <c>null</c>。**</summary>
    public static string? ContentTypeOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(extension)
            ? null
            : ContentTypes.GetValueOrDefault(AttachmentPolicy.NormalizeExtension(extension));
    }

    /// <summary>配信してよい型か。**DB に入っている値を信じ切らない。**</summary>
    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null && ContentTypes.Values.Contains(contentType, StringComparer.Ordinal);

    /// <summary>1 枚を検査する。**通れば空。**</summary>
    /// <remarks>
    /// **ウイルススキャンは掛けない。** 掛けるべき場面（回答者が上げる添付）とは
    /// 相手も経路も違い、ここで有効にすると管理画面の保存がスキャナの生死に引きずられる。
    /// </remarks>
    public static Task<ImmutableArray<AttachmentRejection>> InspectAsync(
        IncomingAttachment image,
        CancellationToken cancellationToken = default) =>
        new AttachmentInspector(Policy).InspectAsync([image], cancellationToken);
}
