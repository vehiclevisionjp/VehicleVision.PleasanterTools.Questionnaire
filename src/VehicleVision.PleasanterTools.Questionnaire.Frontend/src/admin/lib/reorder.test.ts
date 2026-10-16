import { describe, expect, it } from 'vitest';
import { movePageTo, moveQuestionTo } from './reorder';
import type { Page, Question, SurveyDefinition } from './types';

function question(questionId: string): Question {
  return {
    questionId,
    type: 'Text',
    title: { ja: questionId },
    isRequired: false,
    choices: [],
    settings: {},
  };
}

function page(pageId: string, questions: Question[] = []): Page {
  return { pageId, title: { ja: pageId }, questions };
}

function definition(...pages: Page[]): SurveyDefinition {
  return {
    surveyId: 's1',
    version: 1,
    fallbackLanguage: 'ja',
    title: { ja: '見本' },
    displayMode: 'Paged',
    showProgress: true,
    allowEditingAfterSubmit: false,
    pages,
  };
}

describe('moveQuestionTo', () => {
  it('設問 ID を保ったまま別ページの位置へ移す', () => {
    const survey = definition(
      page('p1', [question('q1'), question('q2')]),
      page('p2', [question('q3')]),
    );

    const moved = moveQuestionTo(survey, 'q2', { pageId: 'p2', questionIndex: 1 });

    expect(moved.pages.map((entry) => entry.questions.map((item) => item.questionId))).toEqual([
      ['q1'],
      ['q3', 'q2'],
    ]);
    expect(moved.pages[1]?.questions[1]).toBe(survey.pages[0]?.questions[1]);
  });
});

describe('movePageTo', () => {
  it('ページ ID と設問を保ったまま指定位置へ移す', () => {
    const survey = definition(
      page('p1', [question('q1')]),
      page('p2', [question('q2')]),
      page('p3', [question('q3')]),
    );

    const moved = movePageTo(survey, 'p3', 0);

    expect(moved.pages.map((entry) => entry.pageId)).toEqual(['p3', 'p1', 'p2']);
    expect(moved.pages[0]).toBe(survey.pages[2]);
  });
});
