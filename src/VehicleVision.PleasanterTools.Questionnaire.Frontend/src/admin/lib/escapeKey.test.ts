import { describe, expect, it } from 'vitest';
import { isEditingTarget, shouldGoBackOnEscape } from './escapeKey';

function event(overrides: Partial<Parameters<typeof shouldGoBackOnEscape>[0]> = {}) {
  return {
    key: 'Escape',
    defaultPrevented: false,
    isComposing: false,
    target: { tagName: 'BODY' },
    ...overrides,
  };
}

describe('shouldGoBackOnEscape', () => {
  it('入力欄の外で Esc を押したら戻る', () => {
    expect(shouldGoBackOnEscape(event())).toBe(true);
    expect(shouldGoBackOnEscape(event({ target: { tagName: 'BUTTON' } }))).toBe(true);
  });

  it('Esc 以外のキーでは戻らない', () => {
    expect(shouldGoBackOnEscape(event({ key: 'Enter' }))).toBe(false);
  });

  // 確認ダイアログが Esc を受け持った（preventDefault 済み）あと、window まで届いた同じ Esc で
  // 戻る処理が走り、ダイアログが開き直されていた
  it('すでに別の処理が受け持った Esc では戻らない', () => {
    expect(shouldGoBackOnEscape(event({ defaultPrevented: true }))).toBe(false);
  });

  it('日本語入力の変換中の Esc では戻らない', () => {
    expect(shouldGoBackOnEscape(event({ isComposing: true }))).toBe(false);
  });

  it('入力欄・テキストエリア・プルダウンの中の Esc では戻らない', () => {
    for (const tagName of ['INPUT', 'TEXTAREA', 'SELECT', 'input']) {
      expect(shouldGoBackOnEscape(event({ target: { tagName } })), tagName).toBe(false);
    }
  });

  it('編集可能な領域の中の Esc では戻らない', () => {
    expect(
      shouldGoBackOnEscape(event({ target: { tagName: 'DIV', isContentEditable: true } })),
    ).toBe(false);
  });
});

describe('isEditingTarget', () => {
  it('要素でないものは編集中とみなさない', () => {
    expect(isEditingTarget(null)).toBe(false);
    expect(isEditingTarget(undefined)).toBe(false);
    expect(isEditingTarget('INPUT')).toBe(false);
  });
});
