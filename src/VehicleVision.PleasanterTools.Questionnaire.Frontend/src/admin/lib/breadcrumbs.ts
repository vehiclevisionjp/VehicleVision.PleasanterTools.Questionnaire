export type AdminPage =
  | 'surveys'
  | 'survey-editor'
  | 'audit-logs'
  | 'outbox'
  | 'notifications'
  | 'users'
  | 'saml-settings'
  | 'help'
  | 'account';

export interface Breadcrumb {
  page: AdminPage;
  path: string | null;
}

/**
 * 管理画面で現在地までに通った画面を組み立てる。
 *
 * 一覧以外の管理機能は一覧と並列なので、一覧を経由したようには見せない。
 */
export function buildBreadcrumbs(page: AdminPage): Breadcrumb[] {
  const current: Breadcrumb = { page, path: null };

  if (page === 'survey-editor') {
    return [{ page: 'surveys', path: '/admin' }, current];
  }

  return [current];
}

/** 題名がパンくずを押し広げないよう、表示する文字数を制限する。 */
export function truncateBreadcrumbTitle(title: string, maximumLength = 32): string {
  const characters = Array.from(title);
  return characters.length <= maximumLength
    ? title
    : `${characters.slice(0, maximumLength).join('')}…`;
}
