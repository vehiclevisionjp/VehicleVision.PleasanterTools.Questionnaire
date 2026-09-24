/**
 * Pleasanter のログインで管理画面へ入る流れの、画面に依らない部分（Issue #464）。
 *
 * **判断はここへ置き、`.svelte` には置かない。** `.svelte` は CodeQL の解析対象外で、
 * 単体テスト（vitest）も `lib/` の純粋な関数だけを見る決まりのため。
 */

/** 確認の入口が返す本文。 */
export type PleasanterSsoCheckResponse =
  | { status: 'unauthenticated' }
  | { status: 'signedIn'; next: 'done' | 'totp' | 'enroll' };

/** 失敗の理由。**画面の文言を選ぶためだけに使う。** */
export type PleasanterSsoFailure =
  | 'unknown-user'
  | 'disabled'
  | 'setup-required'
  | 'upstream-error'
  | 'unavailable'
  | 'rate-limited'
  | 'error';

/** 確認の結果を、画面の次の一手へ直したもの。 */
export type PleasanterSsoCheckOutcome =
  | { kind: 'signedIn'; next: 'done' | 'totp' | 'enroll' }
  | { kind: 'waiting' }
  | { kind: 'failed'; reason: PleasanterSsoFailure };

/** `api.ts` の `Result` と同じ形（依存を増やさないため、ここで必要な分だけ書く）。 */
type CheckResult =
  | { ok: true; value: PleasanterSsoCheckResponse }
  | { ok: false; status: number; body?: unknown };

/** 問い合わせの間隔。**サーバの専用のレート制限（5 分に 300 回）に余裕を持たせる。** */
export const POLL_INTERVAL_MS = 2000;

/** 待つ長さの上限。**開きっぱなしで問い合わせ続けない。** */
export const POLL_TIMEOUT_MS = 5 * 60 * 1000;

/**
 * 「自分でログアウトした」印を置く場所。
 *
 * **ログアウトした直後に、ログイン画面が自動で入り直さないためのもの。**
 * Pleasanter のログインは残っているので、印が無いと本アプリのログアウトが効かないように見える。
 * **釦を押したら外す。** タブをまたいで効くよう localStorage に置く。
 */
export const SUPPRESS_AUTO_KEY = 'questionnaire.admin.pleasanterSso.suppressAuto';

/** 確認の結果を読み分ける。 */
export function classifyCheck(result: CheckResult): PleasanterSsoCheckOutcome {
  if (result.ok) {
    const value = result.value;
    if (value?.status === 'signedIn') {
      const next = value.next === 'totp' || value.next === 'enroll' ? value.next : 'done';
      return { kind: 'signedIn', next };
    }

    if (value?.status === 'unauthenticated') {
      return { kind: 'waiting' };
    }

    return { kind: 'failed', reason: 'error' };
  }

  const code =
    typeof result.body === 'object' && result.body !== null && 'code' in result.body
      ? String((result.body as { code: unknown }).code)
      : '';

  switch (code) {
    case 'unknown-user':
    case 'disabled':
    case 'setup-required':
    case 'upstream-error':
      return { kind: 'failed', reason: code };
  }

  if (result.status === 404) return { kind: 'failed', reason: 'unavailable' };
  if (result.status === 429) return { kind: 'failed', reason: 'rate-limited' };
  if (result.status === 503) return { kind: 'failed', reason: 'upstream-error' };
  return { kind: 'failed', reason: 'error' };
}

/** ログイン画面を開いたとき、黙って一度だけ確かめてよいか。 */
export function shouldAutoCheck(state: {
  enabled: boolean;
  setupRequired: boolean;
  pending: boolean;
  suppressed: boolean;
}): boolean {
  // **最初の管理者を作る画面・2 要素の途中・自分でログアウトした直後は確かめない**
  return state.enabled && !state.setupRequired && !state.pending && !state.suppressed;
}

/** まだ待ってよいか。 */
export function shouldKeepPolling(startedAt: number, now: number): boolean {
  return now - startedAt < POLL_TIMEOUT_MS;
}

/**
 * 印を読む。
 *
 * **保存領域が使えない環境では「印なし」として扱う**（プライベートブラウズなど）。
 */
export function readSuppressed(storage: Pick<Storage, 'getItem'> | null | undefined): boolean {
  try {
    return storage?.getItem(SUPPRESS_AUTO_KEY) === '1';
  } catch {
    return false;
  }
}

/** 印を置く・外す。**使えない環境では何もしない。** */
export function writeSuppressed(
  storage: Pick<Storage, 'setItem' | 'removeItem'> | null | undefined,
  suppressed: boolean,
): void {
  try {
    if (suppressed) {
      storage?.setItem(SUPPRESS_AUTO_KEY, '1');
    } else {
      storage?.removeItem(SUPPRESS_AUTO_KEY);
    }
  } catch {
    // 保存できなくても、ログインそのものは続けられる
  }
}

/**
 * ブラウザで開いてよい URL か。
 *
 * **サーバで検証済みだが、画面でも `javascript:` などを開かない。**
 * 同じホストの相対パス（`/` 始まり、`//` は除く）か http(s) の絶対 URL だけ。
 */
export function isSafeBrowserUrl(url: string | null | undefined): url is string {
  if (!url) return false;
  if (url.startsWith('/')) {
    return !url.startsWith('//') && !url.startsWith('/\\');
  }

  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' || parsed.protocol === 'http:';
  } catch {
    return false;
  }
}
