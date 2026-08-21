using System.Collections.Immutable;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>説明文ブロックの段落の種類。</summary>
/// <remarks>
/// **増やすときは画面側の組み立ても同時に足すこと。**
/// 画面が知らない種類が来たら、その段落は出さない（平文にも落とさない）。
/// </remarks>
public enum NoteBlockKind
{
    /// <summary>段落。</summary>
    Paragraph,

    /// <summary>見出し。</summary>
    Heading,

    /// <summary>点の付く箇条書き。</summary>
    BulletList,

    /// <summary>番号の付く箇条書き。</summary>
    NumberedList,
}

/// <summary>説明文ブロックの文字装飾の種類。</summary>
public enum NoteInlineKind
{
    /// <summary>装飾なし。</summary>
    Text,

    /// <summary>太字。</summary>
    Bold,

    /// <summary>斜体。</summary>
    Italic,

    /// <summary>リンク。</summary>
    Link,
}

/// <summary>説明文ブロックの中の文字列 1 片。</summary>
/// <param name="Kind">装飾の種類。</param>
/// <param name="Text">表示する文字列。**平文。記法もタグも含まない**。</param>
/// <param name="Href">
/// リンク先。<see cref="NoteInlineKind.Link"/> のときだけ入る。
/// **<c>https:</c> だけ**（<see cref="IsAllowedHref"/>）。
/// </param>
public sealed record NoteInline(NoteInlineKind Kind, string Text, string? Href = null)
{
    /// <summary>リンク先として受け付けてよいか。</summary>
    /// <remarks>
    /// **許すのは絶対 URL の <c>https:</c> だけ。**
    /// <c>javascript:</c> は言うまでもなく、<c>data:</c> も入れない。
    /// **相対 URL も受け付けない。** 本アプリの中を指させると、
    /// 管理画面の口を回答者に踏ませる導線を作れてしまう。
    /// </remarks>
    public static bool IsAllowedHref(string? href) =>
        !string.IsNullOrWhiteSpace(href)
        && Uri.TryCreate(href, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>箇条書きの項目 1 つ。</summary>
/// <param name="Inlines">項目の中身。</param>
/// <remarks>**入れ子の箇条書きは持たない。** 前書きに要らないうえ、深さの制限が要る。</remarks>
public sealed record NoteListItem(ImmutableArray<NoteInline> Inlines)
{
    /// <remarks>
    /// **既定の <see cref="ImmutableArray{T}"/> を空へ寄せる。**
    /// 初期化していない <c>ImmutableArray</c> は列挙で落ちる。
    /// JSON から読んだ値がここを通るので、入口で潰しておく。
    /// </remarks>
    public ImmutableArray<NoteInline> Inlines { get; init; } =
        Inlines.IsDefault ? [] : Inlines;
}

/// <summary>説明文ブロックの段落 1 つ。</summary>
/// <param name="Kind">段落の種類。</param>
/// <param name="Inlines">
/// 段落・見出しの中身。<see cref="NoteBlockKind.Paragraph"/> と
/// <see cref="NoteBlockKind.Heading"/> で使う。
/// </param>
/// <param name="Items">箇条書きの項目。箇条書きの 2 種類で使う。</param>
/// <param name="Level">見出しの深さ（2 〜 4）。**見出しはここでしか使わない**。</param>
/// <remarks>
/// <para>
/// **HTML の文字列は一切持たない**（Issue #108）。管理者が書いた HTML をそのまま
/// 画面へ流すと、**管理者アカウントを 1 つ奪われただけで、回答画面に任意の
/// スクリプトを載せられる。** 回答画面は完全匿名を掲げているので、
/// そこへ第三者のコードが載る経路を作ってはいけない。
/// </para>
/// <para>
/// **「危ないものを消す」方式は採らない。** 消し漏らしが 1 つでもあれば破れる。
/// **「許した形だけを組み立てる」方式なら、漏れても出るのは平文**になる。
/// </para>
/// </remarks>
public sealed record NoteBlock(
    NoteBlockKind Kind,
    ImmutableArray<NoteInline> Inlines = default,
    ImmutableArray<NoteListItem> Items = default,
    int Level = 0)
{
    /// <summary>見出しの深さの下限。</summary>
    /// <remarks>
    /// **<c>h1</c> は使わない。** 回答画面ではアンケートの題名が最上位で、
    /// 説明文がそれと並ぶと読み上げの見出し構造が壊れる。
    /// </remarks>
    public const int MinimumHeadingLevel = 2;

    /// <summary>見出しの深さの上限。</summary>
    public const int MaximumHeadingLevel = 4;

    /// <inheritdoc cref="NoteListItem.Inlines"/>
    public ImmutableArray<NoteInline> Inlines { get; init; } =
        Inlines.IsDefault ? [] : Inlines;

    /// <inheritdoc cref="NoteListItem.Inlines"/>
    public ImmutableArray<NoteListItem> Items { get; init; } =
        Items.IsDefault ? [] : Items;
}
