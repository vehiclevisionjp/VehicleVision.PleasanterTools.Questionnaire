import { describe, expect, it, vi } from 'vitest';
import { focusFirstError } from './focusError';

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
