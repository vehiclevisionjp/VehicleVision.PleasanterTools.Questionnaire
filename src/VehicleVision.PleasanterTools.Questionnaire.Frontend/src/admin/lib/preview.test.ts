import { describe, expect, it } from 'vitest';
import { listDesignPreviewPages } from './preview';
import type { Page } from './types';

function page(pageId: string, title?: Page['title']): Page {
  return { pageId, title, questions: [] };
}

describe('listDesignPreviewPages', () => {
  it('分岐に関わらず定義順の全ページを返す', () => {
    expect(
      listDesignPreviewPages(
        [page('first', { ja: '最初' }), page('branch', { ja: '分岐先' })],
        'ja',
        (number) => `ページ ${number}`,
      ),
    ).toEqual([
      { pageIndex: 0, pageId: 'first', title: '最初' },
      { pageIndex: 1, pageId: 'branch', title: '分岐先' },
    ]);
  });

  it('見出しが無いページは言語に応じた番号で表す', () => {
    expect(
      listDesignPreviewPages([page('first'), page('second')], 'en', (number) => `Page ${number}`),
    ).toEqual([
      { pageIndex: 0, pageId: 'first', title: 'Page 1' },
      { pageIndex: 1, pageId: 'second', title: 'Page 2' },
    ]);
  });
});
