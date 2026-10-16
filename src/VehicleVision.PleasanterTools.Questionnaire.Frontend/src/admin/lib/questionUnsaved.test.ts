import { describe, expect, it } from 'vitest';
import { hasUnsavedQuestion } from './questionUnsaved';
import type { Question } from './types';

const savedQuestion: Question = {
  questionId: '11111111-1111-1111-1111-111111111111',
  type: 'Text',
  title: { ja: 'お名前' },
  choices: [],
  settings: {},
  isRequired: true,
};

describe('設問の未保存判定', () => {
  it('保存済みの内容と同じ設問を未保存にしない', () => {
    expect(hasUnsavedQuestion({ ...savedQuestion }, savedQuestion)).toBe(false);
  });

  it('設問の内容が変わったときだけ未保存にする', () => {
    expect(
      hasUnsavedQuestion(
        { ...savedQuestion, title: { ja: '氏名' } },
        savedQuestion,
      ),
    ).toBe(true);
  });

  it('保存済みに無い新規設問を未保存にする', () => {
    expect(hasUnsavedQuestion(savedQuestion, undefined)).toBe(true);
  });
});
