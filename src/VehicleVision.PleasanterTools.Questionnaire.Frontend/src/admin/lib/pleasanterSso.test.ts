import { describe, expect, it } from 'vitest';
import {
  classifyCheck,
  isSafeBrowserUrl,
  POLL_TIMEOUT_MS,
  readSuppressed,
  shouldAutoCheck,
  shouldKeepPolling,
  SUPPRESS_AUTO_KEY,
  writeSuppressed,
} from './pleasanterSso';

class MemoryStorage {
  values = new Map<string, string>();
  getItem(key: string) {
    return this.values.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.values.set(key, value);
  }
  removeItem(key: string) {
    this.values.delete(key);
  }
}

describe('classifyCheck', () => {
  it('入れたら次の段階を返す', () => {
    expect(classifyCheck({ ok: true, value: { status: 'signedIn', next: 'done' } })).toEqual({
      kind: 'signedIn',
      next: 'done',
    });
    expect(classifyCheck({ ok: true, value: { status: 'signedIn', next: 'totp' } })).toEqual({
      kind: 'signedIn',
      next: 'totp',
    });
    expect(classifyCheck({ ok: true, value: { status: 'signedIn', next: 'enroll' } })).toEqual({
      kind: 'signedIn',
      next: 'enroll',
    });
  });

  it('まだ Pleasanter にログインしていなければ待つ', () => {
    expect(classifyCheck({ ok: true, value: { status: 'unauthenticated' } })).toEqual({
      kind: 'waiting',
    });
  });

  it('サーバが返した理由の印で失敗を分ける', () => {
    for (const code of ['unknown-user', 'disabled', 'setup-required', 'upstream-error'] as const) {
      expect(classifyCheck({ ok: false, status: 403, body: { code } })).toEqual({
        kind: 'failed',
        reason: code,
      });
    }
  });

  it('印が無ければ状態コードで分ける', () => {
    expect(classifyCheck({ ok: false, status: 404 })).toEqual({ kind: 'failed', reason: 'unavailable' });
    expect(classifyCheck({ ok: false, status: 429 })).toEqual({ kind: 'failed', reason: 'rate-limited' });
    expect(classifyCheck({ ok: false, status: 503 })).toEqual({ kind: 'failed', reason: 'upstream-error' });
    expect(classifyCheck({ ok: false, status: 0 })).toEqual({ kind: 'failed', reason: 'error' });
  });

  it('知らない本文を成功と取り違えない', () => {
    expect(
      classifyCheck({ ok: true, value: { status: 'other' } as unknown as { status: 'unauthenticated' } }),
    ).toEqual({ kind: 'failed', reason: 'error' });
  });
});

describe('shouldAutoCheck', () => {
  const base = { enabled: true, setupRequired: false, pending: false, suppressed: false };

  it('有効で印が無ければ確かめる', () => {
    expect(shouldAutoCheck(base)).toBe(true);
  });

  it('自分でログアウトした直後は確かめない', () => {
    expect(shouldAutoCheck({ ...base, suppressed: true })).toBe(false);
  });

  it('最初の管理者を作る画面と 2 要素の途中では確かめない', () => {
    expect(shouldAutoCheck({ ...base, setupRequired: true })).toBe(false);
    expect(shouldAutoCheck({ ...base, pending: true })).toBe(false);
  });

  it('無効なら確かめない', () => {
    expect(shouldAutoCheck({ ...base, enabled: false })).toBe(false);
  });
});

describe('shouldKeepPolling', () => {
  it('上限を過ぎたら止める', () => {
    expect(shouldKeepPolling(0, POLL_TIMEOUT_MS - 1)).toBe(true);
    expect(shouldKeepPolling(0, POLL_TIMEOUT_MS)).toBe(false);
  });
});

describe('ログアウトの印', () => {
  it('置いて読んで外せる', () => {
    const storage = new MemoryStorage();
    expect(readSuppressed(storage)).toBe(false);

    writeSuppressed(storage, true);
    expect(storage.values.get(SUPPRESS_AUTO_KEY)).toBe('1');
    expect(readSuppressed(storage)).toBe(true);

    writeSuppressed(storage, false);
    expect(readSuppressed(storage)).toBe(false);
  });

  it('保存領域が使えなくても落ちない', () => {
    const broken = {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
      removeItem: () => {
        throw new Error('denied');
      },
    };
    expect(readSuppressed(broken)).toBe(false);
    expect(() => writeSuppressed(broken, true)).not.toThrow();
    expect(readSuppressed(null)).toBe(false);
  });
});

describe('isSafeBrowserUrl', () => {
  it('同じホストのパスと http(s) の絶対 URL だけを通す', () => {
    expect(isSafeBrowserUrl('/users/login')).toBe(true);
    expect(isSafeBrowserUrl('https://www.example.jp/users/login')).toBe(true);
    expect(isSafeBrowserUrl('http://localhost:8080/users/login')).toBe(true);
    expect(isSafeBrowserUrl('javascript:alert(1)')).toBe(false);
    expect(isSafeBrowserUrl('//evil.example.com/')).toBe(false);
    expect(isSafeBrowserUrl('/\\evil.example.com')).toBe(false);
    expect(isSafeBrowserUrl('')).toBe(false);
    expect(isSafeBrowserUrl(null)).toBe(false);
  });
});
