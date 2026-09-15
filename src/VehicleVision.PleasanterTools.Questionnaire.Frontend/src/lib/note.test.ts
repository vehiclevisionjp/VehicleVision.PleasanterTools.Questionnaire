import { describe, expect, it } from 'vitest';
import {
  headingLevel,
  isSafeHref,
  noteBlocks,
  renderableBlocks,
  renderableInlines,
} from './note';
import type { NoteBlock } from './types';

/** 説明文ブロックの書式（Issue #108）。 */
describe('noteBlocks', () => {
  const ja: NoteBlock[] = [{ kind: 'Paragraph', inlines: [], items: [], level: 0 }];
  const en: NoteBlock[] = [{ kind: 'Heading', inlines: [], items: [], level: 2 }];

  it('その言語のものを返す', () => {
    expect(noteBlocks({ ja, en }, 'en')).toBe(en);
  });

  it('無い言語は既定の言語へ落とす', () => {
    expect(noteBlocks({ ja }, 'en')).toBe(ja);
  });

  it('何も無ければ空', () => {
    expect(noteBlocks(null, 'ja')).toEqual([]);
    expect(noteBlocks(undefined, 'ja')).toEqual([]);
    expect(noteBlocks({}, 'ja')).toEqual([]);
  });
});

describe('renderableBlocks', () => {
  it('知らない種類の段落は出さない', () => {
    // **消して残りを出すのではなく、知っているものだけを出す**
    const blocks = [
      { kind: 'Paragraph', inlines: [], items: [], level: 0 },
      { kind: 'Script', inlines: [], items: [], level: 0 },
    ] as unknown as NoteBlock[];

    const visible = renderableBlocks(blocks);

    expect(visible).toHaveLength(1);
    expect(visible[0]?.kind).toBe('Paragraph');
  });

  it('未定義でも落ちない', () => {
    expect(renderableBlocks(undefined)).toEqual([]);
  });
});

describe('headingLevel', () => {
  it.each([
    [1, 2],
    [2, 2],
    [3, 3],
    [4, 4],
    [9, 4],
    [0, 2],
    [-3, 2],
  ])('深さ %i は %i に収まる', (given, expected) => {
    expect(headingLevel(given)).toBe(expected);
  });

  it('数でなければ最上位の 2 にする', () => {
    expect(headingLevel(Number.NaN)).toBe(2);
    expect(headingLevel(Number.POSITIVE_INFINITY)).toBe(2);
  });
});

describe('isSafeHref', () => {
  it('https だけを通す', () => {
    expect(isSafeHref('https://example.com/')).toBe(true);
  });

  it.each([
    'http://example.com/',
    'javascript:alert(1)',
    'JavaScript:alert(1)',
    'data:text/html,<script>',
    'vbscript:msgbox',
    '/admin/users',
    '//example.com',
    'example.com',
    '',
    null,
    undefined,
  ])('%s は通さない', (href) => {
    expect(isSafeHref(href)).toBe(false);
  });
});

describe('renderableInlines', () => {
  it('通らないリンクは文字だけ残す', () => {
    // **消すと書き手が誤りに気付けない**
    const inlines = renderableInlines([
      { kind: 'Link', text: '踏ませたい', href: 'javascript:alert(1)' },
    ]);

    expect(inlines).toEqual([{ kind: 'Text', text: '踏ませたい' }]);
  });

  it('通るリンクはそのまま残す', () => {
    const link = { kind: 'Link' as const, text: '案内', href: 'https://example.com/' };

    expect(renderableInlines([link])).toEqual([link]);
  });

  it('空の文字は出さない', () => {
    expect(renderableInlines([{ kind: 'Text', text: '' }])).toEqual([]);
  });

  it('未定義でも落ちない', () => {
    expect(renderableInlines(undefined)).toEqual([]);
  });
});
