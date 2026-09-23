import { describe, expect, it } from 'vitest';
import { buildBreadcrumbs, truncateBreadcrumbTitle } from './breadcrumbs';

describe('buildBreadcrumbs', () => {
  it('アンケート編集では一覧から現在地までの段を組み立てる', () => {
    expect(buildBreadcrumbs('survey-editor')).toEqual([
      { page: 'surveys', path: '/admin' },
      { page: 'survey-editor', path: null },
    ]);
  });

  it.each([
    'survey-editor',
    'audit-logs',
    'outbox',
    'notifications',
    'users',
    'app-settings',
    'saml-settings',
    'help',
    'account',
  ] as const)('%s では一覧へのパンくずを先頭に置く', (page) => {
    expect(buildBreadcrumbs(page)).toEqual([
      { page: 'surveys', path: '/admin' },
      { page, path: null },
    ]);
  });
});

describe('truncateBreadcrumbTitle', () => {
  it('上限を超える題名を省略記号付きで切り詰める', () => {
    expect(truncateBreadcrumbTitle('あいうえおかきくけこ', 5)).toBe('あいうえお…');
  });

  it('上限以内の題名はそのまま返す', () => {
    expect(truncateBreadcrumbTitle('Survey', 32)).toBe('Survey');
  });
});
