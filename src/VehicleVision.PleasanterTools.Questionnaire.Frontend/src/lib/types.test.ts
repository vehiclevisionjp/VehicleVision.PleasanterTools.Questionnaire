import { describe, expect, it } from 'vitest';
import type { AnswerState, Question, QuestionType } from './types';
import {
  allowsMultiplePerRow,
  hasChoices,
  hasRows,
  hasSelectionRange,
  isDisplayOnly,
  rowValues,
  text,
} from './types';

function question(type: QuestionType): Question {
  return {
    questionId: 'q1',
    type,
    title: { ja: '設問' },
    isRequired: false,
    choices: [],
    settings: {},
  };
}

describe('text', () => {
  it('その言語のものを返す', () => {
    expect(text({ ja: '日本語', en: 'English' }, 'en')).toBe('English');
  });

  // **無い言語は既定の言語へ落とす**（鍵や空白を画面に出さない）
  it('無い言語は既定の言語へ落とす', () => {
    expect(text({ ja: '日本語' }, 'en')).toBe('日本語');
  });

  it('どちらも無ければ空文字', () => {
    expect(text({ fr: 'français' }, 'en')).toBe('');
    expect(text(undefined, 'ja')).toBe('');
    expect(text({}, 'ja')).toBe('');
  });

  it('空文字が入っていればそれを返す', () => {
    expect(text({ en: '', ja: '日本語' }, 'en')).toBe('');
  });
});

describe('設問の形式の判定', () => {
  it('選択肢を持つ形式を見分ける', () => {
    for (const type of [
      'Radio',
      'Checkbox',
      'Dropdown',
      'Grid',
      'CheckboxGrid',
      'Ranking',
    ] as QuestionType[]) {
      expect(hasChoices(question(type)), type).toBe(true);
    }

    for (const type of ['Text', 'Note', 'Date', 'Scale'] as QuestionType[]) {
      expect(hasChoices(question(type)), type).toBe(false);
    }
  });

  it('行を持つ形式を見分ける', () => {
    expect(hasRows(question('Grid'))).toBe(true);
    expect(hasRows(question('CheckboxGrid'))).toBe(true);
    expect(hasRows(question('Ranking'))).toBe(false);
    expect(hasRows(question('Radio'))).toBe(false);
  });

  it('1 行に複数選べる形式を見分ける', () => {
    expect(allowsMultiplePerRow(question('CheckboxGrid'))).toBe(true);
    expect(allowsMultiplePerRow(question('Grid'))).toBe(false);
  });

  // **ランキングは並べた順が答え。**「いくつ選ぶか」とは別の話なので対象外
  it('選べる数を指定できる形式を見分ける', () => {
    expect(hasSelectionRange(question('Checkbox'))).toBe(true);
    expect(hasSelectionRange(question('CheckboxGrid'))).toBe(true);
    expect(hasSelectionRange(question('Ranking'))).toBe(false);
    expect(hasSelectionRange(question('Radio'))).toBe(false);
  });

  it('表示専用の要素を見分ける', () => {
    expect(isDisplayOnly(question('Note'))).toBe(true);
    expect(isDisplayOnly(question('Text'))).toBe(false);
  });
});

describe('rowValues', () => {
  const answer: AnswerState = {
    values: [],
    otherText: '',
    rows: { r1: ['a', 'b'] },
  };

  it('その行の値を返す', () => {
    expect(rowValues(answer, 'r1')).toEqual(['a', 'b']);
  });

  it('無い行は空', () => {
    expect(rowValues(answer, 'r9')).toEqual([]);
  });

  it('回答そのものが無くても空を返す', () => {
    expect(rowValues(undefined, 'r1')).toEqual([]);
    expect(rowValues({ values: [], otherText: '' }, 'r1')).toEqual([]);
  });
});
