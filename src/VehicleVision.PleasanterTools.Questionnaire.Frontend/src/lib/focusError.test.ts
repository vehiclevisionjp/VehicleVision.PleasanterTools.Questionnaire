import { describe, expect, it, vi } from 'vitest';
import { focusFirstError, focusPageHeading, focusSubmitResult, type FocusRoot } from './focusError';

/** DOM を持たない環境でも試せるよう、`querySelector` だけを持つ代役で確かめる。 */
function root(field: unknown): ParentNode {
  return { querySelector: (selector: string) => (selector === 'fieldset.has-error' ? field : null) } as unknown as ParentNode;
}

describe('最初の誤りへのフォーカス', () => {
  it('誤りのある最初の設問の最初の入力へ移す', () => {
    const focus = vi.fn();
    const field = { querySelector: () => ({ focus }) };

    expect(focusFirstError(root(field))).toBe(true);
    expect(focus).toHaveBeenCalledOnce();
  });

  it('誤りのある設問が無ければ何もしない', () => {
    expect(focusFirstError(root(null))).toBe(false);
  });

  it('設問に入力が見つからなければ何もしない', () => {
    expect(focusFirstError(root({ querySelector: () => null }))).toBe(false);
  });
});

function focusRoot(parts: Record<string, unknown>, activeElement: unknown = null, body: unknown = 'BODY'): FocusRoot {
  return { querySelector: (selector: string) => parts[selector] ?? null, activeElement, body };
}

describe('ページが変わったあとのフォーカス（Issue #900）', () => {
  it('ページの見出しへ移す', () => {
    const heading = { focus: vi.fn(), setAttribute: vi.fn() };

    expect(focusPageHeading(focusRoot({ 'main h2': heading, 'main h1': { focus: vi.fn() } }))).toBe(true);
    expect(heading.setAttribute).toHaveBeenCalledWith('tabindex', '-1');
    expect(heading.focus).toHaveBeenCalledOnce();
  });

  it('ページの見出しが無ければ題名へ移す', () => {
    const title = { focus: vi.fn(), setAttribute: vi.fn() };

    expect(focusPageHeading(focusRoot({ 'main h1': title }))).toBe(true);
    expect(title.focus).toHaveBeenCalledOnce();
  });

  it('見出しが無ければ何もしない', () => {
    expect(focusPageHeading(focusRoot({}))).toBe(false);
  });
});

describe('送信のあとのフォーカス（Issue #900）', () => {
  it('フォーカスが body なら、誤りの一覧へ移す', () => {
    const message = { focus: vi.fn(), setAttribute: vi.fn() };
    const root = focusRoot({ 'nav.actions': {}, 'form > .error[role="alert"]': message }, 'BODY');

    expect(focusSubmitResult(root)).toBe(true);
    expect(message.focus).toHaveBeenCalledOnce();
  });

  it('誤りの一覧が無ければ送信釦へ移す', () => {
    const button = { focus: vi.fn() };
    const root = focusRoot({ 'nav.actions': {}, 'nav.actions button:not([disabled])': button }, null);

    expect(focusSubmitResult(root)).toBe(true);
    expect(button.focus).toHaveBeenCalledOnce();
  });

  it('すでにどこかにフォーカスがあれば何もしない', () => {
    const button = { focus: vi.fn() };
    const root = focusRoot({ 'nav.actions': {}, 'nav.actions button:not([disabled])': button }, { tag: 'input' });

    expect(focusSubmitResult(root)).toBe(false);
    expect(button.focus).not.toHaveBeenCalled();
  });

  it('送信の画面が無ければ（完了画面など）何もしない', () => {
    expect(focusSubmitResult(focusRoot({}, 'BODY'))).toBe(false);
  });
});
