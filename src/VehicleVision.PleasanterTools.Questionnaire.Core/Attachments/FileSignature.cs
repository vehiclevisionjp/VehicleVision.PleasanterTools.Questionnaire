using System.Collections.Frozen;
using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Attachments;

/// <summary>拡張子と、その形式が先頭に持つバイト列の対応。</summary>
/// <remarks>
/// **拡張子は名乗りでしかない。** 中身と食い違っていないかを見るために使う
/// （<c>_documents/非機能設計.md</c> 1 章）。
/// これも万能ではない。正しい形式のファイルに悪意ある内容は埋められる。
/// </remarks>
public static class FileSignature
{
    private static readonly FrozenDictionary<string, ImmutableArray<byte[]>> Signatures =
        new Dictionary<string, ImmutableArray<byte[]>>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = [[0x25, 0x50, 0x44, 0x46]],                      // %PDF
            [".png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
            [".jpg"] = [[0xFF, 0xD8, 0xFF]],
            [".jpeg"] = [[0xFF, 0xD8, 0xFF]],
            [".gif"] = [[0x47, 0x49, 0x46, 0x38]],                      // GIF8
            [".webp"] = [[0x52, 0x49, 0x46, 0x46]],                     // RIFF（12 バイト目以降に WEBP）
            [".zip"] = [[0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x05, 0x06]],
            // Office の新形式は実体が ZIP
            [".docx"] = [[0x50, 0x4B, 0x03, 0x04]],
            [".xlsx"] = [[0x50, 0x4B, 0x03, 0x04]],
            [".pptx"] = [[0x50, 0x4B, 0x03, 0x04]],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>先頭バイトを照合できる拡張子か。</summary>
    /// <remarks>
    /// <c>.txt</c> や <c>.csv</c> のように決まった先頭バイトを持たない形式もある。
    /// **照合できないことと、安全であることは別。**
    /// </remarks>
    public static bool CanVerify(string extension) => Signatures.ContainsKey(extension);

    /// <summary>中身の先頭バイトが拡張子と一致するか。照合できない拡張子では <c>true</c>。</summary>
    public static bool Matches(string extension, ReadOnlySpan<byte> content)
    {
        if (!Signatures.TryGetValue(extension, out var candidates))
        {
            return true;
        }

        foreach (var signature in candidates)
        {
            if (content.Length >= signature.Length
                && content[..signature.Length].SequenceEqual(signature))
            {
                return true;
            }
        }

        return false;
    }
}
