using System.Collections.Immutable;
using System.Text;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Text;

/// <summary>説明文ブロックの記法を、決まった形の入れ子へ変換する。</summary>
/// <remarks>
/// <para>
/// **受け付けるものをここが決める。素通しの経路は作らない**（Issue #108）。
/// 管理者が何を書いても、出てくるのは <see cref="NoteBlock"/> の入れ子だけ。
/// **知らない記法は平文として落ちる。** 消し漏れて危ないものが残ることはない。
/// </para>
/// <para>
/// **Markdown の一部だけを真似る。** 全部を実装しない。
/// 前書き（会社の説明や注意書き）に要るのは、太字・斜体・リンク・箇条書き・見出しだけ。
/// 表も画像も引用もコードも受け付けない。**受け付ける形が少ないほど、
/// 「これで全部か」を人が確かめられる。**
/// </para>
/// <list type="bullet">
///   <item><c>## 見出し</c>（<c>#</c> 2 〜 4 個。1 個は使わない）</item>
///   <item><c>- 項目</c>（点の付く箇条書き）</item>
///   <item><c>1. 項目</c>（番号の付く箇条書き）</item>
///   <item>空行で段落を分ける</item>
///   <item><c>**太字**</c> / <c>*斜体*</c> / <c>[文字](https://…)</c></item>
/// </list>
/// </remarks>
public static class NoteMarkup
{
    /// <summary>受け付ける記法の文字数の上限。</summary>
    /// <remarks>
    /// **上限が無いと定義の JSON が際限なく膨らむ。**
    /// 定義は公開のたびにスナップショットとして DB へ入る
    /// （<c>_documents/データモデル設計.md</c> 2.1）。
    /// </remarks>
    public const int MaximumLength = 4000;

    /// <summary>記法を段落の並びへ変換する。</summary>
    /// <remarks>
    /// **落ちない。** どんな文字列でも段落の並びを返す（空になることはある）。
    /// 記法の誤りは「装飾が付かない」という形で現れる。
    /// **書き手が直せるように、消さずに平文で残す。**
    /// </remarks>
    public static ImmutableArray<NoteBlock> Parse(string? markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return [];
        }

        if (markup.Length > MaximumLength)
        {
            markup = markup[..MaximumLength];
        }

        var lines = markup.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = ImmutableArray.CreateBuilder<NoteBlock>();
        var paragraph = new List<string>();
        var items = ImmutableArray.CreateBuilder<NoteListItem>();
        var listKind = NoteBlockKind.BulletList;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            // **1 つの段落の中の改行は空白へ潰す。** 改行そのものを持たせると、
            // 画面の幅で折り返す仕組みと二重になって収まりが読めなくなる
            var inlines = ParseInlines(string.Join(' ', paragraph));
            paragraph.Clear();
            if (inlines.Length > 0)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, Inlines: inlines));
            }
        }

        void FlushList()
        {
            if (items.Count == 0)
            {
                return;
            }

            blocks.Add(new NoteBlock(listKind, Items: items.ToImmutable()));
            items.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (TryReadHeading(line, out var level, out var headingText))
            {
                FlushParagraph();
                FlushList();
                var inlines = ParseInlines(headingText);
                if (inlines.Length > 0)
                {
                    blocks.Add(new NoteBlock(
                        NoteBlockKind.Heading, Inlines: inlines, Level: level));
                }

                continue;
            }

            if (TryReadListItem(line, out var kind, out var itemText))
            {
                FlushParagraph();

                // **種類が変わったら別の箇条書きにする。** 点と番号が 1 つの並びに混ざらない
                if (items.Count > 0 && listKind != kind)
                {
                    FlushList();
                }

                listKind = kind;
                var inlines = ParseInlines(itemText);
                if (inlines.Length > 0)
                {
                    items.Add(new NoteListItem(inlines));
                }

                continue;
            }

            FlushList();
            paragraph.Add(line);
        }

        FlushParagraph();
        FlushList();
        return blocks.ToImmutable();
    }

    /// <summary>段落の並びを記法へ戻す。</summary>
    /// <remarks>
    /// **管理画面が編集し直せるようにするため。** 原文を別に保存すると、
    /// 構造と原文の 2 か所が真実になり、片方だけ直る事故が起きる。
    /// **持つのは構造だけにして、書き戻しはここで作る。**
    /// </remarks>
    public static string Render(ImmutableArray<NoteBlock> blocks)
    {
        if (blocks.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            switch (block.Kind)
            {
                case NoteBlockKind.Heading:
                    var level = Math.Clamp(
                        block.Level, NoteBlock.MinimumHeadingLevel, NoteBlock.MaximumHeadingLevel);
                    builder.Append('#', level).Append(' ');
                    RenderInlines(builder, block.Inlines);
                    builder.Append('\n');
                    break;

                case NoteBlockKind.BulletList:
                case NoteBlockKind.NumberedList:
                    var number = 1;
                    foreach (var item in block.Items)
                    {
                        builder.Append(block.Kind == NoteBlockKind.BulletList
                            ? "- "
                            : $"{number++}. ");
                        RenderInlines(builder, item.Inlines);
                        builder.Append('\n');
                    }

                    break;

                default:
                    RenderInlines(builder, block.Inlines);
                    builder.Append('\n');
                    break;
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static void RenderInlines(StringBuilder builder, ImmutableArray<NoteInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case NoteInlineKind.Bold:
                    builder.Append("**").Append(inline.Text).Append("**");
                    break;
                case NoteInlineKind.Italic:
                    builder.Append('*').Append(inline.Text).Append('*');
                    break;
                case NoteInlineKind.Link:
                    builder.Append('[').Append(inline.Text)
                        .Append("](").Append(inline.Href).Append(')');
                    break;
                default:
                    builder.Append(inline.Text);
                    break;
            }
        }
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        // **`#` 1 つは見出しにしない。** 回答画面ではアンケートの題名が最上位で、
        // 説明文がそれと並ぶと読み上げの見出し構造が壊れる
        if (hashes < NoteBlock.MinimumHeadingLevel
            || hashes >= line.Length
            || line[hashes] != ' ')
        {
            return false;
        }

        level = Math.Min(hashes, NoteBlock.MaximumHeadingLevel);
        text = line[(hashes + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryReadListItem(string line, out NoteBlockKind kind, out string text)
    {
        kind = NoteBlockKind.BulletList;
        text = string.Empty;

        if (line.StartsWith("- ", StringComparison.Ordinal))
        {
            text = line[2..].Trim();
            return text.Length > 0;
        }

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= line.Length
            || line[digits] != '.' || line[digits + 1] != ' ')
        {
            return false;
        }

        kind = NoteBlockKind.NumberedList;
        text = line[(digits + 2)..].Trim();
        return text.Length > 0;
    }

    private static ImmutableArray<NoteInline> ParseInlines(string text)
    {
        var inlines = ImmutableArray.CreateBuilder<NoteInline>();
        var plain = new StringBuilder();
        var index = 0;

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }

            inlines.Add(new NoteInline(NoteInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        while (index < text.Length)
        {
            if (TryReadEmphasis(text, index, "**", out var boldText, out var boldEnd))
            {
                FlushPlain();
                inlines.Add(new NoteInline(NoteInlineKind.Bold, boldText));
                index = boldEnd;
                continue;
            }

            if (TryReadEmphasis(text, index, "*", out var italicText, out var italicEnd))
            {
                FlushPlain();
                inlines.Add(new NoteInline(NoteInlineKind.Italic, italicText));
                index = italicEnd;
                continue;
            }

            if (TryReadLink(text, index, out var link, out var linkEnd))
            {
                FlushPlain();
                inlines.Add(link);
                index = linkEnd;
                continue;
            }

            plain.Append(text[index]);
            index++;
        }

        FlushPlain();
        return inlines.ToImmutable();
    }

    private static bool TryReadEmphasis(
        string text, int start, string marker, out string inner, out int end)
    {
        inner = string.Empty;
        end = start;

        if (!text.AsSpan(start).StartsWith(marker, StringComparison.Ordinal))
        {
            return false;
        }

        var open = start + marker.Length;
        var close = text.IndexOf(marker, open, StringComparison.Ordinal);
        if (close < 0 || close == open)
        {
            return false;
        }

        inner = text[open..close];

        // **装飾の中に装飾は入れない。** 入れ子を許すと、どこまでが 1 つの装飾かを
        // 読み手が数えることになる。前書きにそこまでの表現力は要らない
        if (inner.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        end = close + marker.Length;
        return true;
    }

    private static bool TryReadLink(string text, int start, out NoteInline link, out int end)
    {
        link = new NoteInline(NoteInlineKind.Text, string.Empty);
        end = start;

        if (text[start] != '[')
        {
            return false;
        }

        var labelEnd = text.IndexOf(']', start + 1);
        if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(')
        {
            return false;
        }

        var hrefEnd = text.IndexOf(')', labelEnd + 2);
        if (hrefEnd < 0)
        {
            return false;
        }

        var label = text[(start + 1)..labelEnd].Trim();
        var href = text[(labelEnd + 2)..hrefEnd].Trim();
        if (label.Length == 0)
        {
            return false;
        }

        // **`https:` でなければリンクにしない。** ここで弾いた分は文字として残る
        link = NoteInline.IsAllowedHref(href)
            ? new NoteInline(NoteInlineKind.Link, label, href)
            : new NoteInline(NoteInlineKind.Text, label);
        end = hrefEnd + 1;
        return true;
    }
}
