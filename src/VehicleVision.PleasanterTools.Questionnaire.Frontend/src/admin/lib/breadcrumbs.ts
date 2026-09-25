import { adminUrl } from './adminPath';

export type AdminPage =
  | 'surveys'
  | 'survey-editor'
  | 'audit-logs'
  | 'outbox'
  | 'notifications'
  | 'users'
  | 'app-settings'
  | 'saml-settings'
  | 'pleasanter-sso-settings'
  | 'help'
  | 'account';

export interface Breadcrumb {
  page: AdminPage;
  path: string | null;
}

/**
 * 管理画面で現在地までに通った画面を組み立てる。
 *
 * 一覧以外の管理機能も、一覧へ戻る入口を共通にするため一覧を先頭に置く。
 */
export function buildBreadcrumbs(page: AdminPage): Breadcrumb[] {
  const current: Breadcrumb = { page, path: null };

  if (page !== 'surveys') {
    return [{ page: 'surveys', path: adminUrl() }, current];
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
