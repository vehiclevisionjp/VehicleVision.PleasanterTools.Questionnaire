import { describe, expect, it } from 'vitest';
import {
  API_TOKEN_DRAFT_KEY,
  API_TOKEN_DRAFT_LIFETIME_MS,
  saveApiTokenDraft,
  takeApiTokenDraft,
  takeReauthMarker,
} from './apiTokens';

/** `sessionStorage` の代わり。 */
function memoryStorage() {
  const map = new Map<string, string>();
  return {
    map,
    getItem: (key: string) => map.get(key) ?? null,
    setItem: (key: string, value: string) => void map.set(key, value),
    removeItem: (key: string) => void map.delete(key),
  };
}

describe('強制再認証の間の、入力の途中の値（Issue #1034）', () => {
  const draft = { name: 'ci', scopes: ['surveys.read'], expiresInDays: 30, ipText: '203.0.113.0/24' };

  it('置いた値を、戻ったあとに 1 回だけ読める', () => {
    const storage = memoryStorage();
    saveApiTokenDraft(draft, 1_000, storage);

    expect(takeApiTokenDraft(2_000, storage)).toEqual(draft);
    expect(takeApiTokenDraft(2_000, storage)).toBeNull();
  });

  it('読み出すのは決まった項目だけ（秘密が紛れ込んでも返さない）', () => {
    const storage = memoryStorage();
    saveApiTokenDraft({ ...draft, token: 'qat_secret', password: 'secret' } as never, 1_000, storage);

    expect(Object.keys(takeApiTokenDraft(2_000, storage)!).sort()).toEqual([
      'expiresInDays',
      'ipText',
      'name',
      'scopes',
    ]);
  });

  it('古い値・形の違う値・壊れた値は使わず、消す', () => {
    const old = memoryStorage();
    saveApiTokenDraft(draft, 1_000, old);
    expect(takeApiTokenDraft(1_000 + API_TOKEN_DRAFT_LIFETIME_MS + 1, old)).toBeNull();
    expect(old.map.size).toBe(0);

    for (const raw of ['not json', '{}', '{"savedAt":1,"draft":{"name":1}}', 'null']) {
      const storage = memoryStorage();
      storage.map.set(API_TOKEN_DRAFT_KEY, raw);
      expect(takeApiTokenDraft(2_000, storage)).toBeNull();
      expect(storage.map.size).toBe(0);
    }
  });

  it('未来の時刻に置かれた値は信用しない', () => {
    const storage = memoryStorage();
    saveApiTokenDraft(draft, 10_000_000, storage);
    expect(takeApiTokenDraft(1_000, storage)).toBeNull();
  });

  it('storage を使えない環境でも落ちない', () => {
    expect(() => saveApiTokenDraft(draft, 1_000, null)).not.toThrow();
    expect(takeApiTokenDraft(1_000, null)).toBeNull();

    const blocked = () => {
      throw new Error('blocked');
    };
    const throwing = { getItem: blocked, setItem: blocked, removeItem: blocked };
    expect(() => saveApiTokenDraft(draft, 1_000, throwing)).not.toThrow();
    expect(takeApiTokenDraft(1_000, throwing)).toBeNull();
  });
});

describe('IdP から戻った印（Issue #1034）', () => {
  function read(search: string, hash = '') {
    const replaced: string[] = [];
    const marker = takeReauthMarker({ pathname: '/admin/me', search, hash }, (url) =>
      replaced.push(url),
    );
    return { marker, replaced };
  }

  it('done・failed を読み、URL から消す', () => {
    expect(read('?apiTokenReauth=done')).toEqual({ marker: 'done', replaced: ['/admin/me'] });
    expect(read('?apiTokenReauth=failed')).toEqual({ marker: 'failed', replaced: ['/admin/me'] });
  });

  it('ほかの query と hash は残す', () => {
    expect(read('?a=1&apiTokenReauth=done', '#x').replaced).toEqual(['/admin/me?a=1#x']);
  });

  it('印が無ければ何もしない。知らない値は印として扱わない（URL からは消す）', () => {
    expect(read('?a=1')).toEqual({ marker: null, replaced: [] });
    expect(read('?apiTokenReauth=evil')).toEqual({ marker: null, replaced: ['/admin/me'] });
  });
});
