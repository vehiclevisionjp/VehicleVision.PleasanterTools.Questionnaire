import { describe, expect, it } from 'vitest';
import type { Translate } from './i18n/messages';
import type { AnswerState, Question } from './types';
import { validatePage, validateQuestion } from './validation';

/**
 * 画面側の検証の試験（Issue #110）。
 *
 * **文言そのものは見ない。** 見るのは「どの規則で弾いたか」。
 * 文言を突き合わせると、言い回しを直すたびに試験が落ちる。
 */

/** 鍵をそのまま返す。**どの規則に当たったかだけを見る。** */
const t: Translate = ((key: string) => key) as unknown as Translate;

function question(id: string, extra: Partial<Question> = {}): Question {
  return {
    questionId: id,
    type: 'Text',
    title: { ja: id },
    isRequired: false,
    choices: [],
    settings: {},
    ...extra,
  };
}

function answer(values: string[]): AnswerState {
  return { values, otherText: '' };
}

/** 指定した大きさの添付を作る。**中身は見ないので 0 埋めで足りる。** */
function file(name: string, size: number): File {
  return new File([new Uint8Array(size)], name);
}

function attached(files: File[]): AnswerState {
  return { values: [], otherText: '', files };
}

describe('validateQuestion', () => {
  it('表示専用の要素は何も見ない', () => {
    expect(validateQuestion(question('q', { type: 'Note', isRequired: true }), undefined, t, 'ja')).toBeNull();
  });

  it('必須で未回答なら弾く', () => {
    expect(validateQuestion(question('q', { isRequired: true }), undefined, t, 'ja')).toBe(
      'validation.required',
    );
  });

  it('空白だけの答えは未回答として扱う', () => {
    expect(validateQuestion(question('q', { isRequired: true }), answer(['   ']), t, 'ja')).toBe(
      'validation.required',
    );
  });

  it('任意で未回答なら通す', () => {
    expect(validateQuestion(question('q'), undefined, t, 'ja')).toBeNull();
  });

  it('文字数の上限を超えたら弾く', () => {
    const target = question('q', { settings: { maxLength: 3 } });

    expect(validateQuestion(target, answer(['abc']), t, 'ja')).toBeNull();
    expect(validateQuestion(target, answer(['abcd']), t, 'ja')).toBe('validation.tooLong');
  });

  it('メール形式を見る', () => {
    const target = question('q', { settings: { format: 'Email' } });

    expect(validateQuestion(target, answer(['a@example.com']), t, 'ja')).toBeNull();
    expect(validateQuestion(target, answer(['example.com']), t, 'ja')).toBe('validation.email');
  });

  it('URL は http と https だけ通す', () => {
    const target = question('q', { settings: { format: 'Url' } });

    expect(validateQuestion(target, answer(['https://example.com']), t, 'ja')).toBeNull();
    // **`javascript:` を通さない。** 通すと、集めた URL を管理画面で開いたときに危ない
    expect(validateQuestion(target, answer(['javascript:alert(1)']), t, 'ja')).toBe('validation.url');
  });

  it('尺度は範囲の外を弾く', () => {
    const target = question('q', {
      type: 'Scale',
      settings: { scaleMinimum: 1, scaleMaximum: 5 },
    });

    expect(validateQuestion(target, answer(['3']), t, 'ja')).toBeNull();
    expect(validateQuestion(target, answer(['0']), t, 'ja')).toBe('validation.minimum');
    expect(validateQuestion(target, answer(['6']), t, 'ja')).toBe('validation.maximum');
    expect(validateQuestion(target, answer(['三']), t, 'ja')).toBe('validation.notANumber');
  });

  it('日付として読めない値を弾く', () => {
    const target = question('q', { type: 'Date' });

    expect(validateQuestion(target, answer(['2026-08-22']), t, 'ja')).toBeNull();
    expect(validateQuestion(target, answer(['きのう']), t, 'ja')).toBe('validation.date');
  });

  it('ランキングで同じ項目が 2 回出たら弾く', () => {
    const target = question('q', {
      type: 'Ranking',
      choices: [
        { value: 'a', label: { ja: 'a' }, isOther: false },
        { value: 'b', label: { ja: 'b' }, isOther: false },
      ],
    });

    // **並べた順そのものが答え。** 重複すると順位が決まらない
    expect(validateQuestion(target, answer(['a', 'a']), t, 'ja')).toBe('validation.duplicateRank');
    expect(validateQuestion(target, answer(['a', 'b']), t, 'ja')).toBeNull();
  });

  it('選択肢を持たない形式に値が 2 つ来たら弾く', () => {
    expect(validateQuestion(question('q'), answer(['x', 'y']), t, 'ja')).toBe(
      'validation.singleValueOnly',
    );
  });

  it('添付の個数とサイズの上限を見る', () => {
    const target = question('q', {
      type: 'File',
      settings: { maxFileCount: 1, maxFileSizeBytes: 100 },
    });

    const small = file('a.txt', 10);
    const large = file('b.txt', 200);

    expect(validateQuestion(target, attached([small]), t, 'ja')).toBeNull();
    expect(
      validateQuestion(target, attached([small, small]), t, 'ja'),
    ).toBe('validation.fileCount');
    expect(validateQuestion(target, attached([large]), t, 'ja')).toBe(
      'validation.fileTooLarge',
    );
  });

  it('必須の添付は値ではなくファイルの有無で見る', () => {
    const target = question('q', { type: 'File', isRequired: true });

    expect(validateQuestion(target, answer(['何か']), t, 'ja')).toBe('validation.fileRequired');
  });

  it('グリッドの必須は行が全部埋まっていること', () => {
    const target = question('q', {
      type: 'Grid',
      isRequired: true,
      choices: [{ value: 'c1', label: { ja: 'c1' }, isOther: false }],
      settings: {
        rows: [
          { rowId: 'r1', label: { ja: 'r1' } },
          { rowId: 'r2', label: { ja: 'r2' } },
        ],
      },
    });

    const partial: AnswerState = { values: [], otherText: '', rows: { r1: ['c1'] } };
    const full: AnswerState = { values: [], otherText: '', rows: { r1: ['c1'], r2: ['c1'] } };

    // **1 行でも空なら足りない。** 一部だけ答えて送れると、どこまで答えたのか分からない
    expect(validateQuestion(target, partial, t, 'ja')).toBe('validation.rowRequired');
    expect(validateQuestion(target, full, t, 'ja')).toBeNull();
  });

  it('単一選択のグリッドは 1 行に 2 つ来たら弾く', () => {
    const target = question('q', {
      type: 'Grid',
      choices: [
        { value: 'c1', label: { ja: 'c1' }, isOther: false },
        { value: 'c2', label: { ja: 'c2' }, isOther: false },
      ],
      settings: { rows: [{ rowId: 'r1', label: { ja: 'r1' } }] },
    });

    const two: AnswerState = { values: [], otherText: '', rows: { r1: ['c1', 'c2'] } };

    expect(validateQuestion(target, two, t, 'ja')).toBe('validation.singleValueOnly');
  });
});

describe('validatePage', () => {
  it('弾いた設問だけを返す', () => {
    const questions = [
      question('q1', { isRequired: true }),
      question('q2'),
      question('q3', { isRequired: true }),
    ];

    const errors = validatePage(questions, { q3: answer(['答えた']) }, t, 'ja');

    expect(Object.keys(errors)).toEqual(['q1']);
  });

  it('問題が無ければ空を返す', () => {
    expect(validatePage([question('q1')], {}, t, 'ja')).toEqual({});
  });
});
