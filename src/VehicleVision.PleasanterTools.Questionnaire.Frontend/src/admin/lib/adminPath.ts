const DEFAULT_ADMIN_PATH = '/admin';
const PLACEHOLDER = '__QUESTIONNAIRE_ADMIN_PATH__';

function readAdminPath(): string {
  if (typeof document === 'undefined') return DEFAULT_ADMIN_PATH;

  const value = document
    .querySelector<HTMLMetaElement>('meta[name="questionnaire-admin-path"]')
    ?.content;
  return value && value !== PLACEHOLDER ? value : DEFAULT_ADMIN_PATH;
}

/** サーバが起動時に埋め込んだ管理画面の入口。 */
export const adminPath = readAdminPath();

/** 管理画面内の相対パスを、設定された入口から始まる URL にする。 */
export function adminUrl(path = '', basePath = adminPath): string {
  if (path !== '' && !path.startsWith('/')) {
    throw new Error('管理画面内のパスは / で始めてください。');
  }

  return `${basePath}${path}`;
}

/** URL が管理画面内なら、入口を除いた相対パスを返す。 */
export function adminRoute(pathname: string, basePath = adminPath): string | null {
  if (pathname === basePath || pathname === `${basePath}/`) return '/';
  if (!pathname.startsWith(`${basePath}/`)) return null;

  const route = pathname.slice(basePath.length);
  return route.length > 1 && route.endsWith('/') ? route.slice(0, -1) : route;
}
