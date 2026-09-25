import { afterEach, describe, expect, it, vi } from 'vitest';
import { submittedCookie } from './api';

/** `document.cookie` への代入値から属性を取り出す。 */
function attribute(cookie: string, name: string): string | undefined {
  return cookie
    .split(';')
    .map((part) => part.trim())
    .find((part) => part.toLowerCase().startsWith(`${name}=`))
    ?.slice(name.length + 1);
}

/** Path 以外に、消すときに一致していないといけない属性も含めて並べる。 */
function scope(cookie: string): string[] {
  return cookie
    .split(';')
    .slice(1)
    .map((part) => part.trim())
    .filter((part) => !part.startsWith('max-age='));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.resetModules();
});

describe('submittedCookie', () => {
  it.each([
    ['サブパス無し', '', '/f/pub-1'],
    ['サブパス有り', '/questionnaire', '/questionnaire/f/pub-1'],
  ])('%s: 置くときと消すときの Path が一致する', (_, base, expected) => {
    const set = submittedCookie('pub-1', 'set', base, 'https:');
    const clear = submittedCookie('pub-1', 'clear', base, 'https:');

    expect(attribute(set, 'path')).toBe(expected);
    expect(attribute(clear, 'path')).toBe(expected);
    expect(scope(clear)).toEqual(scope(set));
  });

  it('置くときは期限付き、消すときは期限 0', () => {
    expect(submittedCookie('pub-1', 'set', '', 'http:')).toBe(
      'q.a.pub-1=1; path=/f/pub-1; max-age=15552000; samesite=lax',
    );
    expect(submittedCookie('pub-1', 'clear', '', 'http:')).toBe(
      'q.a.pub-1=; path=/f/pub-1; max-age=0; samesite=lax',
    );
  });

  it('公開 ID は Path 上でエスケープする', () => {
    expect(attribute(submittedCookie('a b', 'clear', '/q', 'http:'), 'path')).toBe('/q/f/a%20b');
  });
});

describe('markSubmitted / forgetSubmission', () => {
  it.each([
    ['サブパス無し', '__QUESTIONNAIRE_BASE_PATH__', '/f/pub-1'],
    ['サブパス有り', '/questionnaire', '/questionnaire/f/pub-1'],
  ])('%s: 配信時に埋め込まれたサブパスで置き、同じ Path で消す', async (_, meta, expected) => {
    const written: string[] = [];
    vi.stubGlobal('document', {
      querySelector: () => ({ content: meta }),
      set cookie(value: string) {
        written.push(value);
      },
      get cookie() {
        return '';
      },
    });
    vi.stubGlobal('location', { protocol: 'https:' });
    vi.stubGlobal('window', {
      localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
    });

    // **サブパスは読み込み時に meta 要素から決まる。** 差し替えた document で読み直す
    const { markSubmitted, forgetSubmission } = await import('./api');
    markSubmitted('pub-1');
    forgetSubmission('pub-1');

    expect(written).toHaveLength(2);
    const [set = '', clear = ''] = written;
    expect(attribute(set, 'path')).toBe(expected);
    expect(attribute(clear, 'path')).toBe(expected);
    expect(attribute(clear, 'max-age')).toBe('0');
    expect(scope(clear)).toEqual(scope(set));
  });
});
