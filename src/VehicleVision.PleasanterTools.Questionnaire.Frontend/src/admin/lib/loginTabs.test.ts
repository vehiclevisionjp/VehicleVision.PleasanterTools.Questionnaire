import { describe, expect, it } from 'vitest';
import {
  availableMethods,
  LOGIN_METHODS,
  nextTabIndex,
  panelId,
  resolveSelected,
  shouldShowTabs,
  tabId,
} from './loginTabs';

/**
 * DesignReference の `scripts/check-login-tabs.mjs` の契約のうち、
 * 描画に依らない部分（方式の数・初期選択・キー操作・id）を確かめる。
 * ARIA の属性と DOM の描画は `SignInPanel.svelte` 側で組み、画面で確かめる。
 */
describe('availableMethods', () => {
  it('SSO・ID/パスワードの順に並べる', () => {
    expect(availableMethods({ ssoAvailable: true, passwordAvailable: true })).toEqual(['sso', 'local']);
  });

  it('パスキーはサーバが使えると返したときだけ、最後に並べる', () => {
    expect(
      availableMethods({ ssoAvailable: true, passwordAvailable: true, passkeyAvailable: true }),
    ).toEqual(['sso', 'local', 'passkey']);
    expect(
      availableMethods({ ssoAvailable: false, passwordAvailable: true, passkeyAvailable: true }),
    ).toEqual(['local', 'passkey']);
    expect(
      availableMethods({ ssoAvailable: false, passwordAvailable: true, passkeyAvailable: false }),
    ).toEqual(['local']);
  });

  it('使えない方式は並べない', () => {
    expect(availableMethods({ ssoAvailable: true, passwordAvailable: false })).toEqual(['sso']);
    expect(availableMethods({ ssoAvailable: false, passwordAvailable: true })).toEqual(['local']);
    expect(availableMethods({ ssoAvailable: false, passwordAvailable: false })).toEqual([]);
  });
});

describe('shouldShowTabs', () => {
  it('2 方式以上のときだけタブにする（0・1 方式は通常表示）', () => {
    expect(shouldShowTabs([])).toBe(false);
    expect(shouldShowTabs(['sso'])).toBe(false);
    expect(shouldShowTabs(['local'])).toBe(false);
    expect(shouldShowTabs(['sso', 'local'])).toBe(true);
  });
});

describe('resolveSelected', () => {
  it('指定が無ければ先頭（SSO）を開く', () => {
    expect(resolveSelected(['sso', 'local'], undefined)).toBe('sso');
    expect(resolveSelected(['sso', 'local'], null)).toBe('sso');
  });

  it('使える方式の指定は保つ', () => {
    expect(resolveSelected(['sso', 'local'], 'local')).toBe('local');
  });

  it('使えない方式の指定は先頭へ落とす（隠した方式を開かない）', () => {
    expect(resolveSelected(['sso'], 'local')).toBe('sso');
    expect(resolveSelected(['local'], 'sso')).toBe('local');
  });

  it('方式が無ければ null', () => {
    expect(resolveSelected([], 'sso')).toBeNull();
  });
});

describe('nextTabIndex', () => {
  it('左右キーは循環する', () => {
    expect(nextTabIndex('ArrowRight', 0, 2)).toBe(1);
    expect(nextTabIndex('ArrowRight', 1, 2)).toBe(0);
    expect(nextTabIndex('ArrowLeft', 0, 2)).toBe(1);
    expect(nextTabIndex('ArrowLeft', 1, 2)).toBe(0);
  });

  it('Home は先頭、End は末尾へ移る', () => {
    expect(nextTabIndex('Home', 1, 2)).toBe(0);
    expect(nextTabIndex('End', 0, 2)).toBe(1);
  });

  it('ほかのキーや方式が無いときは何もしない', () => {
    expect(nextTabIndex('Enter', 0, 2)).toBeNull();
    expect(nextTabIndex('ArrowDown', 0, 2)).toBeNull();
    expect(nextTabIndex('ArrowRight', 0, 0)).toBeNull();
  });
});

describe('id', () => {
  it('方式ごとに一意で、タブと区画が結び付く', () => {
    const ids = LOGIN_METHODS.flatMap((method) => [panelId(method), tabId(method)]);
    expect(new Set(ids).size).toBe(ids.length);
    expect(tabId('sso')).toBe('login-sso-tab');
    expect(panelId('local')).toBe('login-local');
  });
});
