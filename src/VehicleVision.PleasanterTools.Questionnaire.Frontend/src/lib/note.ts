import type { NoteBlock, NoteBlockKind, NoteInline } from './types';
import { DEFAULT_LANGUAGE, type Language } from './i18n/language';

/** 画面が組み立てられる段落の種類。**ここに無い値は出さない。** */
const RENDERABLE_KINDS: readonly NoteBlockKind[] = [
  'Paragraph',
  'Heading',
  'BulletList',
  'NumberedList',
];

/** 見出しの深さの下限。**アンケートの題名より下へ置く。** */
export const MIN_HEADING_LEVEL = 2;

/** 見出しの深さの上限。 */
export const MAX_HEADING_LEVEL = 4;

/**
 * 説明文ブロックの本文から、その言語の段落の並びを取り出す（Issue #108）。
 *
 * **その言語が無ければ既定の言語へ落とす。** 何も無ければ空。
 */
export function noteBlocks(
  byLanguage: Record<string, NoteBlock[]> | null | undefined,
  language: Language,
): NoteBlock[] {
  if (!byLanguage) return [];
  return byLanguage[language] ?? byLanguage[DEFAULT_LANGUAGE] ?? [];
}

/**
 * 画面が組み立てられる形だけを残す。
 *
 * **サーバが作った値しか来ない前提に寄りかからない。**
 * 知らない種類が混ざったら、その段落ごと出さない。
 * **「消して残りを出す」ではなく「知っているものだけ出す」。**
 */
export function renderableBlocks(blocks: NoteBlock[] | undefined): NoteBlock[] {
  if (!blocks) return [];
  return blocks.filter((block) => RENDERABLE_KINDS.includes(block.kind));
}

/**
 * 見出しの深さを 2 〜 4 に収める。
 *
 * **`h1` を作らせない。** 回答画面ではアンケートの題名が最上位で、
 * 説明文がそこへ並ぶと読み上げの見出し構造が壊れる。
 */
export function headingLevel(level: number): number {
  if (!Number.isFinite(level)) return MIN_HEADING_LEVEL;
  return Math.min(Math.max(Math.trunc(level), MIN_HEADING_LEVEL), MAX_HEADING_LEVEL);
}

/**
 * リンクとして出してよい行き先か。
 *
 * **サーバも同じ判断をしている**（`NoteInline.IsAllowedHref`）。
 * **二重に確かめるのは、画面が受け取る JSON の出どころを 1 つに限れないため。**
 * 通すのは絶対 URL の `https:` だけ。
 */
export function isSafeHref(href: string | null | undefined): href is string {
  if (!href) return false;
  try {
    return new URL(href).protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * 文字装飾を、画面が扱える形へ落とす。
 *
 * **行き先が通らないリンクは、文字だけを残す。**
 * 消してしまうと、書いた人が誤りに気付けない。
 */
export function renderableInlines(inlines: NoteInline[] | undefined): NoteInline[] {
  if (!inlines) return [];
  return inlines
    .filter((inline) => inline.text !== '')
    .map((inline) =>
      inline.kind === 'Link' && !isSafeHref(inline.href)
        ? { kind: 'Text' as const, text: inline.text }
        : inline,
    );
}
