import { text } from '../../lib/types';
import type { Language } from '../../lib/i18n/language';
import type { Page } from './types';

/** 意匠確認で直接開けるページ。 */
export interface DesignPreviewPage {
  pageIndex: number;
  pageId: string;
  title: string;
}

/**
 * 意匠確認用に、分岐によらない定義上の全ページを並べる。
 *
 * **ページ番号は配列の順序で決める。** 分岐による到達順ではないので、
 * 回答の有無で一覧が変わらない。
 */
export function listDesignPreviewPages(
  pages: readonly Page[],
  language: Language,
  pageLabel: (number: number) => string,
  fallbackLanguage: Language = 'ja',
): DesignPreviewPage[] {
  return pages.map((page, pageIndex) => ({
    pageIndex,
    pageId: page.pageId,
    title: text(page.title, language, fallbackLanguage) || pageLabel(pageIndex + 1),
  }));
}
