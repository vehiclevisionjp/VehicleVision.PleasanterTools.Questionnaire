/**
 * 本アプリを置いたサブパス（例 `/questionnaire`）。**サブパスが無ければ空文字。**（Issue #465）
 *
 * **組み立て時に焼き込まない。** サーバが配信時に meta 要素へ埋める（`HtmlShell.cs`）。
 * 同じ成果物を `/` にもサブパスにも置けるようにするため。
 *
 * ⚠️ **サーバ・画面の経路はすべてこの値から始める。** `/api/...` を直に書くと、
 * サブパス配下に置いたとき同じホストの別アプリ（Pleasanter）へ要求が飛ぶ。
 */
const PLACEHOLDER = '__QUESTIONNAIRE_BASE_PATH__';

/** meta 要素の値を、使える形に直す。**おかしな値は無かったものとして扱う。** */
export function normalizeBasePath(value: string | null | undefined): string {
  // **印が差し替わっていないのは開発サーバ（vite）。** サブパスは無い
  if (!value || value === PLACEHOLDER) return '';

  const trimmed = value.replace(/\/+$/, '');
  // **自サイト内の経路だけを受け付ける。** `//example.com` のような書き方は外す
  if (trimmed === '' || !trimmed.startsWith('/') || trimmed.startsWith('//')) return '';
  return trimmed;
}

function readBasePath(): string {
  if (typeof document === 'undefined') return '';

  return normalizeBasePath(
    document.querySelector<HTMLMetaElement>('meta[name="questionnaire-base-path"]')?.content,
  );
}

/** サーバが配信時に埋め込んだサブパス。 */
export const basePath = readBasePath();

/** 本アプリの経路（`/api/...` や `/f/...`）を、サブパスから始まる URL にする。 */
export function appUrl(path: string, base = basePath): string {
  if (!path.startsWith('/')) {
    throw new Error('本アプリ内のパスは / で始めてください。');
  }

  return `${base}${path}`;
}

/** URL が本アプリ内なら、サブパスを除いた経路を返す。**外なら `null`。** */
export function appRoute(pathname: string, base = basePath): string | null {
  if (base === '') return pathname;
  if (pathname === base) return '/';
  return pathname.startsWith(`${base}/`) ? pathname.slice(base.length) : null;
}
