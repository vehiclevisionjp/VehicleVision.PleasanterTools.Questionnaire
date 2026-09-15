import { beforeEach, describe, expect, it, vi } from 'vitest';
import { clearDraft, DRAFT_MAX_AGE_DAYS, hasDraft, readDraft, saveDraft } from './draft';
import type { AnswerState } from './types';

/**
 * 下書きの試験（Issue #110）。
 *
 * **端末の中だけに置く**という前提が崩れていないか、
 * **壊れた値・古い値を掴まない**かを見る。
 */

/** `localStorage` の代わり。**Node には無い。** */
function fakeStorage(): Storage {
  const map = new Map<string, string>();

  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (key: string) => map.get(key) ?? null,
    key: (index: number) => [...map.keys()][index] ?? null,
    removeItem: (key: string) => void map.delete(key),
    setItem: (key: string, value: string) => void map.set(key, value),
  };
}

const PUBLIC_ID = 'abc123';
const KEY = `questionnaire.draft.${PUBLIC_ID}`;

let store: Storage;

beforeEach(() => {
  store = fakeStorage();
  vi.stubGlobal('localStorage', store);
});

function answer(values: string[], extra: Partial<AnswerState> = {}): AnswerState {
  return { values, otherText: '', ...extra } as AnswerState;
}

describe('saveDraft / readDraft', () => {
  it('書いたものを読み戻せる', () => {
    saveDraft(PUBLIC_ID, { q1: answer(['はい']) });

    expect(readDraft(PUBLIC_ID)?.q1?.values).toEqual(['はい']);
  });

  it('空の設問だけなら何も置かない', () => {
    saveDraft(PUBLIC_ID, { q1: answer(['   ']) });

    // **開いただけの状態を「下書きあり」にしない**
    expect(store.getItem(KEY)).toBeNull();
    expect(hasDraft(PUBLIC_ID)).toBe(false);
  });

  it('全部消したら置いてある下書きも消す', () => {
    saveDraft(PUBLIC_ID, { q1: answer(['はい']) });
    saveDraft(PUBLIC_ID, { q1: answer(['']) });

    // **前の内容が端末に残らないこと**
    expect(store.getItem(KEY)).toBeNull();
  });

  it('何も選んでいない行は落とす', () => {
    saveDraft(PUBLIC_ID, {
      q1: answer([], { rows: { r1: [''], r2: ['c1'] } }),
    });

    expect(readDraft(PUBLIC_ID)?.q1?.rows).toEqual({ r2: ['c1'] });
  });

  it('添付は保存しない', () => {
    saveDraft(PUBLIC_ID, {
      q1: answer(['はい'], { files: [{ name: 'a.txt', size: 1 }] } as Partial<AnswerState>),
    });

    expect(store.getItem(KEY)).not.toContain('a.txt');
  });
});

describe('readDraft が掴まないもの', () => {
  it('壊れた値は読まずに消す', () => {
    store.setItem(KEY, '{ これは JSON ではない');

    expect(readDraft(PUBLIC_ID)).toBeNull();
    // **読めないものを置いておく理由が無い**
    expect(store.getItem(KEY)).toBeNull();
  });

  it('保存日時が読めなければ消す', () => {
    store.setItem(KEY, JSON.stringify({ savedAt: 'きのう', answers: { q1: { values: ['x'] } } }));

    expect(readDraft(PUBLIC_ID)).toBeNull();
    expect(store.getItem(KEY)).toBeNull();
  });

  it('期限を過ぎたものは消す', () => {
    const old = new Date(Date.now() - (DRAFT_MAX_AGE_DAYS + 1) * 24 * 60 * 60 * 1000);
    store.setItem(
      KEY,
      JSON.stringify({ savedAt: old.toISOString(), answers: { q1: { values: ['x'] } } }),
    );

    expect(readDraft(PUBLIC_ID)).toBeNull();
    expect(store.getItem(KEY)).toBeNull();
  });

  it('期限の内なら読む', () => {
    const recent = new Date(Date.now() - 1000);
    store.setItem(
      KEY,
      JSON.stringify({ savedAt: recent.toISOString(), answers: { q1: { values: ['x'] } } }),
    );

    expect(readDraft(PUBLIC_ID)?.q1?.values).toEqual(['x']);
  });

  it('形の違う値は捨てる', () => {
    store.setItem(
      KEY,
      JSON.stringify({
        savedAt: new Date().toISOString(),
        answers: { q1: { values: ['ok', 1, null, {}], otherText: 42, rows: 'これは行ではない' } },
      }),
    );

    const draft = readDraft(PUBLIC_ID);

    // **端末の中身は誰でも書き換えられる。** 形を確かめてから使う
    expect(draft?.q1?.values).toEqual(['ok']);
    expect(draft?.q1?.otherText).toBe('');
    expect(draft?.q1?.rows).toBeUndefined();
  });
});

describe('Web Storage が使えないとき', () => {
  it('例外を投げずに諦める', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('storage は使えない');
      },
      setItem: () => {
        throw new Error('storage は使えない');
      },
      removeItem: () => {
        throw new Error('storage は使えない');
      },
    });

    // **書けないことを理由に回答を止めない**
    expect(() => saveDraft(PUBLIC_ID, { q1: answer(['はい']) })).not.toThrow();
    expect(readDraft(PUBLIC_ID)).toBeNull();
    expect(() => clearDraft(PUBLIC_ID)).not.toThrow();
  });
});
