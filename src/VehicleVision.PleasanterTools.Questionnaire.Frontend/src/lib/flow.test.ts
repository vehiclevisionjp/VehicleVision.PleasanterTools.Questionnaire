import { describe, expect, it } from 'vitest';
import { toSteps, tracePath } from './flow';
import type { AnswerState, Choice, Page, Question, SurveyDefinition } from './types';

/**
 * 分岐の経路探索の試験（Issue #110）。
 *
 * **ここはサーバ側（`Core/Flow/SurveyFlow.cs`）の写し。**
 * 食い違うと、画面が出した設問がサーバに落とされる（または隠したはずの答えが保存される）。
 * **写しであることを守るための試験**でもある。
 */

function question(id: string, extra: Partial<Question> = {}): Question {
  return {
    questionId: id,
    type: 'Radio',
    title: { ja: id },
    isRequired: false,
    choices: [],
    settings: {},
    ...extra,
  };
}

function choice(value: string, extra: Partial<Choice> = {}): Choice {
  return { value, label: { ja: value }, isOther: false, ...extra };
}

function page(id: string, questions: Question[], extra: Partial<Page> = {}): Page {
  return { pageId: id, questions, ...extra };
}

function definition(pages: Page[]): SurveyDefinition {
  return {
    surveyId: 's',
    version: 1,
    title: { ja: 'アンケート' },
    pages,
    displayMode: 'Paged',
    showProgress: false,
    allowEditingAfterSubmit: false,
  };
}

function answered(values: Record<string, string[]>): Record<string, AnswerState> {
  return Object.fromEntries(
    Object.entries(values).map(([id, list]) => [id, { values: list, otherText: '' }]),
  );
}

describe('tracePath', () => {
  it('定義が無ければ空の経路を返す', () => {
    const path = tracePath(undefined, {});

    expect(path.pages).toHaveLength(0);
    expect(path.visible.size).toBe(0);
  });

  it('分岐が無ければ全ページを順に通る', () => {
    const survey = definition([
      page('p1', [question('q1')]),
      page('p2', [question('q2')]),
      page('p3', [question('q3')]),
    ]);

    const path = tracePath(survey, {});

    expect(path.pages.map((p) => p.pageId)).toEqual(['p1', 'p2', 'p3']);
    expect([...path.visible]).toEqual(['q1', 'q2', 'q3']);
  });

  it('選択肢の行き先でページを飛ばす', () => {
    const survey = definition([
      page('p1', [
        question('q1', {
          choices: [
            choice('skip', { next: { kind: 'Page', pageId: 'p3' } }),
            choice('stay'),
          ],
        }),
      ]),
      page('p2', [question('q2')]),
      page('p3', [question('q3')]),
    ]);

    const path = tracePath(survey, answered({ q1: ['skip'] }));

    expect(path.pages.map((p) => p.pageId)).toEqual(['p1', 'p3']);
    // **飛ばしたページの設問は出さない。** 答えが残っていても無かったことにする
    expect(path.visible.has('q2')).toBe(false);
  });

  it('選択肢の行き先が無ければページ末尾の行き先に落ちる', () => {
    const survey = definition([
      page('p1', [question('q1', { choices: [choice('stay')] })], {
        next: { kind: 'Page', pageId: 'p3' },
      }),
      page('p2', [question('q2')]),
      page('p3', [question('q3')]),
    ]);

    const path = tracePath(survey, answered({ q1: ['stay'] }));

    expect(path.pages.map((p) => p.pageId)).toEqual(['p1', 'p3']);
  });

  it('Submit の行き先でそこまでにする', () => {
    const survey = definition([
      page('p1', [question('q1')], { next: { kind: 'Submit' } }),
      page('p2', [question('q2')]),
    ]);

    expect(tracePath(survey, {}).pages.map((p) => p.pageId)).toEqual(['p1']);
  });

  it('同じページを 2 度通らない（循環しても止まる）', () => {
    const survey = definition([
      page('p1', [question('q1')], { next: { kind: 'Page', pageId: 'p2' } }),
      page('p2', [question('q2')], { next: { kind: 'Page', pageId: 'p1' } }),
    ]);

    // **止まらなければこの試験は終わらない。** 検査を抜けた定義でも画面が固まらないこと
    expect(tracePath(survey, {}).pages.map((p) => p.pageId)).toEqual(['p1', 'p2']);
  });

  it('Equals の条件を満たす設問だけ出す', () => {
    const survey = definition([
      page('p1', [
        question('q1', { choices: [choice('yes'), choice('no')] }),
        question('q2', {
          visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Equals', value: 'yes' }] },
        }),
      ]),
    ]);

    expect(tracePath(survey, answered({ q1: ['yes'] })).visible.has('q2')).toBe(true);
    expect(tracePath(survey, answered({ q1: ['no'] })).visible.has('q2')).toBe(false);
  });

  it('未回答は NotEquals を満たさない', () => {
    const survey = definition([
      page('p1', [
        question('q1'),
        question('q2', {
          visibleWhen: {
            match: 'All',
            rules: [{ questionId: 'q1', operator: 'NotEquals', value: 'yes' }],
          },
        }),
      ]),
    ]);

    // **「まだ答えていない」を「違う値だ」と扱わない。**
    // 扱うと、開いた直後に条件が成立して設問が出てしまう
    expect(tracePath(survey, {}).visible.has('q2')).toBe(false);
  });

  it('空白だけの答えは未回答として扱う', () => {
    const survey = definition([
      page('p1', [
        question('q1', { type: 'Text' }),
        question('q2', {
          visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Answered' }] },
        }),
      ]),
    ]);

    expect(tracePath(survey, answered({ q1: ['   '] })).visible.has('q2')).toBe(false);
  });

  it('Any は 1 つでも満たせば出す', () => {
    const survey = definition([
      page('p1', [
        question('q1'),
        question('q2'),
        question('q3', {
          visibleWhen: {
            match: 'Any',
            rules: [
              { questionId: 'q1', operator: 'Equals', value: 'a' },
              { questionId: 'q2', operator: 'Equals', value: 'b' },
            ],
          },
        }),
      ]),
    ]);

    expect(tracePath(survey, answered({ q2: ['b'] })).visible.has('q3')).toBe(true);
  });

  it('隠れている設問を参照する条件は成立しない', () => {
    const survey = definition([
      page('p1', [
        question('q1'),
        question('q2', {
          visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Equals', value: 'x' }] },
        }),
        question('q3', {
          visibleWhen: { match: 'All', rules: [{ questionId: 'q2', operator: 'Answered' }] },
        }),
      ]),
    ]);

    // **q2 が隠れている以上、その答えは無かったことにする。**
    // 残っている答えで芋づるに設問が出ると、回答者の見た画面と保存内容が食い違う
    expect(tracePath(survey, answered({ q2: ['何か'] })).visible.has('q3')).toBe(false);
  });

  it('GreaterThan は数として比べ、数で読めなければ成立しない', () => {
    const survey = definition([
      page('p1', [
        question('q1', { type: 'Scale' }),
        question('q2', {
          visibleWhen: {
            match: 'All',
            rules: [{ questionId: 'q1', operator: 'GreaterThan', value: '10' }],
          },
        }),
      ]),
    ]);

    expect(tracePath(survey, answered({ q1: ['11'] })).visible.has('q2')).toBe(true);
    expect(tracePath(survey, answered({ q1: ['10'] })).visible.has('q2')).toBe(false);
    expect(tracePath(survey, answered({ q1: ['たくさん'] })).visible.has('q2')).toBe(false);
  });
});

describe('toSteps', () => {
  it('設問が 1 つも出ないページは飛ばす', () => {
    const survey = definition([
      page('p1', [question('q1')]),
      page('p2', [
        question('q2', {
          visibleWhen: { match: 'All', rules: [{ questionId: 'q1', operator: 'Equals', value: 'x' }] },
        }),
      ]),
    ]);

    const steps = toSteps(tracePath(survey, {}), 'Paged');

    // **題名だけの空ページを見せない**
    expect(steps.map((step) => step.page.pageId)).toEqual(['p1']);
  });

  it('1 問 1 ページ表示では設問ごとに区切る', () => {
    const survey = definition([page('p1', [question('q1'), question('q2'), question('q3')])]);

    const steps = toSteps(tracePath(survey, {}), 'OneQuestionPerPage');

    expect(steps).toHaveLength(3);
    expect(steps.every((step) => step.questions.length === 1)).toBe(true);
    // **分岐はページ単位のまま。** どの区切りも元のページを指す
    expect(steps.every((step) => step.page.pageId === 'p1')).toBe(true);
  });
});
